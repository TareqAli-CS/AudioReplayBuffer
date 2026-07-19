using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioReplayBuffer.Core;

/// <summary>
/// Plays a saved replay into a chosen render device — typically a virtual
/// audio cable (Voicemod, VB-CABLE, …) whose paired virtual microphone is
/// selected as the input in Discord or any call app — optionally mirrored
/// to the default speakers so the user hears what their friends hear.
/// </summary>
public sealed class VoicePlayer : IDisposable
{
    private readonly object _lock = new();
    private readonly List<(WasapiOut Output, AudioFileReader Reader)> _active = [];
    private int _playingCount;
    private int _generation;
    private float _volume = 1f;
    private int _mirrorIndex = -1;

    /// <summary>Raised (from a playback thread) when the file finished on all outputs.</summary>
    public event Action? PlaybackEnded;

    public bool IsPlaying { get; private set; }

    public static List<string> ListRenderDevices()
    {
        var names = new List<string>();
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            names.Add(device.FriendlyName);
        return names;
    }

    public void Play(string filePath, string voiceDeviceName, bool alsoSpeakers)
    {
        Stop();
        lock (_lock)
        {
            int generation = _generation;
            using var enumerator = new MMDeviceEnumerator();

            MMDevice? voiceDevice = null;
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                if (voiceDevice == null &&
                    device.FriendlyName.Contains(voiceDeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    voiceDevice = device;
                }
            }
            if (voiceDevice == null)
                throw new InvalidOperationException(
                    $"Playback device \"{voiceDeviceName}\" was not found — pick your virtual mic device again in Settings.");

            var targets = new List<MMDevice> { voiceDevice };
            if (alsoSpeakers)
            {
                var speakers = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                if (speakers.ID != voiceDevice.ID)
                    targets.Add(speakers);
            }

            _mirrorIndex = -1;
            for (int i = 0; i < targets.Count; i++)
            {
                var reader = new AudioFileReader(filePath) { Volume = _volume };
                var output = new WasapiOut(targets[i], AudioClientShareMode.Shared, false, 200);
                output.Init(reader);
                output.PlaybackStopped += (_, _) => OnOutputStopped(generation);
                _active.Add((output, reader));
                if (i == 1)
                    _mirrorIndex = _active.Count - 1; // the speakers copy
            }

            _playingCount = _active.Count;
            foreach (var (output, _) in _active)
                output.Play();
            IsPlaying = true;
        }
    }

    private void OnOutputStopped(int generation)
    {
        lock (_lock)
        {
            // A late event from an already-replaced playback must not touch
            // the current one.
            if (generation != _generation || !IsPlaying)
                return;
            if (--_playingCount > 0)
                return;
            IsPlaying = false;
        }

        PlaybackEnded?.Invoke();
        // Dispose off the playback callback thread, and only if no new
        // playback has started meanwhile.
        Task.Run(() =>
        {
            lock (_lock)
            {
                if (generation == _generation)
                    CleanupLocked();
            }
        });
    }

    /// <summary>
    /// Silences the speakers copy of the current playback immediately (the
    /// virtual-mic copy keeps going). Used when the user unticks "hear it
    /// too" mid-playback because of echo.
    /// </summary>
    public void StopMirror()
    {
        lock (_lock)
        {
            if (!IsPlaying || _mirrorIndex < 0 || _mirrorIndex >= _active.Count)
                return;
            try { _active[_mirrorIndex].Output.Stop(); } catch { }
            _mirrorIndex = -1;
        }
    }

    /// <summary>Sets the playback volume (1.0 = 100%); applies live to current playback.</summary>
    public void SetVolume(float volume)
    {
        lock (_lock)
        {
            _volume = Math.Clamp(volume, 0f, 2f);
            foreach (var (_, reader) in _active)
                reader.Volume = _volume;
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _generation++;
            CleanupLocked();
        }
    }

    private void CleanupLocked()
    {
        IsPlaying = false;
        foreach (var (output, reader) in _active)
        {
            try { output.Stop(); } catch { }
            output.Dispose();
            reader.Dispose();
        }
        _active.Clear();
        _playingCount = 0;
        _mirrorIndex = -1;
    }

    public void Dispose() => Stop();
}
