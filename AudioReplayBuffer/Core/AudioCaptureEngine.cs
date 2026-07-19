using System.Diagnostics;
using AudioReplayBuffer.Configuration;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AudioReplayBuffer.Core;

/// <summary>
/// Owns the WASAPI captures (desktop loopback and/or microphone), converts
/// everything to one common format (48 kHz, 16-bit, stereo), mixes the
/// sources and feeds the ring buffer. Restarts itself when the default
/// audio device changes (e.g. plugging in headphones).
/// </summary>
public sealed class AudioCaptureEngine : IDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
    public const int BytesPerSecond = SampleRate * Channels * 2;
    public static readonly WaveFormat BufferFormat = new(SampleRate, 16, Channels);

    private readonly AppSettings _settings;
    private readonly CircularAudioBuffer _ring;
    private readonly WaveformHistory? _waveform;
    private readonly object _stateLock = new();
    private readonly MMDeviceEnumerator _deviceEnumerator = new();
    private readonly DeviceNotificationClient _notificationClient;

    private IWaveIn? _desktopCapture;
    private WasapiCapture? _micCapture;
    private WasapiOut? _silencePlayer;
    private Thread? _mixerThread;
    private CancellationTokenSource? _cts;
    private System.Threading.Timer? _restartTimer;
    private volatile bool _shouldRun;
    private volatile float _peak;

    public bool IsRunning => _shouldRun;

    /// <summary>Most recent peak sample level (0..1), for UI level meters.</summary>
    public float CurrentPeak => _peak;

    public AudioCaptureEngine(AppSettings settings, CircularAudioBuffer ring, WaveformHistory? waveform = null)
    {
        _settings = settings;
        _ring = ring;
        _waveform = waveform;
        _notificationClient = new DeviceNotificationClient(ScheduleRestart);
        _deviceEnumerator.RegisterEndpointNotificationCallback(_notificationClient);
    }

    public void Start()
    {
        lock (_stateLock)
        {
            StopInternal();
            _cts = new CancellationTokenSource();
            var mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels))
            {
                ReadFully = true
            };

            bool captureDesktop = _settings.Mode is CaptureMode.Desktop or CaptureMode.Both;
            bool captureMic = _settings.Mode is CaptureMode.Microphone or CaptureMode.Both;

            if (captureDesktop)
            {
                if (!string.IsNullOrWhiteSpace(_settings.TargetApp))
                {
                    // Per-app capture: independent of the render device, so
                    // no silence keep-alive is needed either.
                    int pid = ProcessLoopbackCapture.ResolvePid(_settings.TargetApp);
                    _desktopCapture = new ProcessLoopbackCapture(pid, _settings.TargetAppExclude);
                }
                else
                {
                    _desktopCapture = new WasapiLoopbackCapture();
                    if (_settings.KeepAliveSilence)
                        StartSilencePlayer();
                }
                mixer.AddMixerInput(AttachSource(_desktopCapture, _settings.DesktopGain));
            }

            if (captureMic)
            {
                _micCapture = new WasapiCapture();
                mixer.AddMixerInput(AttachSource(_micCapture, _settings.MicrophoneGain));
            }

            _desktopCapture?.StartRecording();
            _micCapture?.StartRecording();

            var token = _cts.Token;
            _mixerThread = new Thread(() => MixerLoop(mixer, token))
            {
                IsBackground = true,
                Name = "AudioMixer"
            };
            _mixerThread.Start();
            _shouldRun = true;
            Logger.Log($"Capture started ({_settings.Mode}).");
        }
    }

    public void Stop()
    {
        lock (_stateLock)
        {
            _shouldRun = false;
            StopInternal();
            Logger.Log("Capture stopped.");
        }
    }

    private void StopInternal()
    {
        _cts?.Cancel();
        _mixerThread?.Join(1500);
        _mixerThread = null;

        try { _desktopCapture?.StopRecording(); } catch { }
        try { _micCapture?.StopRecording(); } catch { }
        try { _silencePlayer?.Stop(); } catch { }

        _desktopCapture?.Dispose();
        _micCapture?.Dispose();
        _silencePlayer?.Dispose();
        _desktopCapture = null;
        _micCapture = null;
        _silencePlayer = null;
        _cts?.Dispose();
        _cts = null;
        _peak = 0f;
    }

    /// <summary>
    /// Wires one capture device into the mixer: buffered, resampled to
    /// 48 kHz, forced to stereo, gain applied.
    /// </summary>
    private ISampleProvider AttachSource(IWaveIn capture, float gain)
    {
        var buffered = new BufferedWaveProvider(capture.WaveFormat)
        {
            BufferDuration = TimeSpan.FromSeconds(10),
            DiscardOnBufferOverflow = true,
            ReadFully = true // returns silence when the device is quiet
        };

        capture.DataAvailable += (_, e) => buffered.AddSamples(e.Buffer, 0, e.BytesRecorded);
        capture.RecordingStopped += (_, e) =>
        {
            if (e.Exception != null)
            {
                Logger.Log($"Capture stopped unexpectedly: {e.Exception.Message}");
                ScheduleRestart();
            }
        };

        ISampleProvider sp = buffered.ToSampleProvider();
        if (sp.WaveFormat.SampleRate != SampleRate)
            sp = new WdlResamplingSampleProvider(sp, SampleRate);
        if (sp.WaveFormat.Channels == 1)
            sp = new MonoToStereoSampleProvider(sp);
        else if (sp.WaveFormat.Channels > Channels)
            sp = new ChannelDownmixProvider(sp);
        if (Math.Abs(gain - 1f) > 0.001f)
            sp = new VolumeSampleProvider(sp) { Volume = gain };
        return sp;
    }

    /// <summary>
    /// Pulls mixed audio at wall-clock rate and writes it to the ring
    /// buffer as 16-bit PCM. Wall-clock pacing (instead of free-running
    /// reads) keeps the buffer timeline aligned with real time even while
    /// every source is silent.
    /// </summary>
    private float _wfPeak;
    private int _wfFrames;

    private void MixerLoop(MixingSampleProvider mixer, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        long framesDone = 0;
        const int maxChunkFrames = SampleRate / 10; // 100 ms
        var floatBuf = new float[maxChunkFrames * Channels];
        var byteBuf = new byte[maxChunkFrames * Channels * 2];

        while (!ct.IsCancellationRequested)
        {
            Thread.Sleep(50);

            long targetFrames = (long)(sw.Elapsed.TotalSeconds * SampleRate);
            long delta = targetFrames - framesDone;
            if (delta <= 0)
                continue;

            // A gap of more than 2 s means the machine slept or the thread
            // was starved; skip the gap instead of flooding zeros.
            if (delta > SampleRate * 2)
            {
                framesDone = targetFrames;
                continue;
            }

            float wakePeak = 0f;
            while (delta > 0 && !ct.IsCancellationRequested)
            {
                int chunkFrames = (int)Math.Min(delta, maxChunkFrames);
                int samples = chunkFrames * Channels;
                int read = mixer.Read(floatBuf, 0, samples);

                float chunkPeak = 0f;
                for (int i = 0; i < read; i++)
                {
                    float v = floatBuf[i];
                    if (v < 0) v = -v;
                    if (v > chunkPeak) chunkPeak = v;
                    var s = (short)Math.Clamp((int)(floatBuf[i] * 32767f), short.MinValue, short.MaxValue);
                    byteBuf[i * 2] = (byte)s;
                    byteBuf[i * 2 + 1] = (byte)(s >> 8);
                }
                if (chunkPeak > wakePeak) wakePeak = chunkPeak;

                _ring.Write(byteBuf, read * 2);
                framesDone += chunkFrames;
                delta -= chunkFrames;

                // One waveform entry per 100 ms of audio.
                if (_waveform != null)
                {
                    if (chunkPeak > _wfPeak) _wfPeak = chunkPeak;
                    _wfFrames += chunkFrames;
                    while (_wfFrames >= SampleRate / WaveformHistory.EntriesPerSecond)
                    {
                        _waveform.Add(_wfPeak);
                        _wfFrames -= SampleRate / WaveformHistory.EntriesPerSecond;
                        _wfPeak = 0f;
                    }
                }
            }
            _peak = wakePeak;
        }
    }

    /// <summary>
    /// WASAPI loopback delivers no data while nothing is playing. Playing
    /// inaudible silence keeps the render stream active so the capture
    /// timeline never stalls.
    /// </summary>
    private void StartSilencePlayer()
    {
        try
        {
            _silencePlayer = new WasapiOut(AudioClientShareMode.Shared, 200);
            _silencePlayer.Init(new SilenceProvider(new WaveFormat(SampleRate, 16, Channels)));
            _silencePlayer.Play();
        }
        catch (Exception ex)
        {
            Logger.Log("Silence keep-alive failed (continuing without it): " + ex.Message);
            _silencePlayer?.Dispose();
            _silencePlayer = null;
        }
    }

    /// <summary>
    /// Debounced restart, triggered by default-device changes or a capture
    /// dying (device unplugged). Windows fires several notifications in a
    /// burst, so wait for them to settle before rebuilding.
    /// </summary>
    private void ScheduleRestart()
    {
        if (!_shouldRun)
            return;
        _restartTimer?.Dispose();
        _restartTimer = new System.Threading.Timer(_ => TryRestart(), null, 800, Timeout.Infinite);
    }

    private void TryRestart()
    {
        if (!_shouldRun)
            return;
        try
        {
            Logger.Log("Audio device change detected — restarting capture.");
            Start();
        }
        catch (Exception ex)
        {
            Logger.Log("Capture restart failed, retrying in 3 s: " + ex.Message);
            _restartTimer?.Dispose();
            _restartTimer = new System.Threading.Timer(_ => TryRestart(), null, 3000, Timeout.Infinite);
        }
    }

    public void Dispose()
    {
        _shouldRun = false;
        try { _deviceEnumerator.UnregisterEndpointNotificationCallback(_notificationClient); } catch { }
        _restartTimer?.Dispose();
        lock (_stateLock)
        {
            StopInternal();
        }
        _deviceEnumerator.Dispose();
    }

    private sealed class DeviceNotificationClient(Action onDefaultDeviceChanged) : IMMNotificationClient
    {
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (role == Role.Multimedia)
                onDefaultDeviceChanged();
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}
