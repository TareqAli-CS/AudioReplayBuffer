using AudioReplayBuffer.Configuration;
using AudioReplayBuffer.UI;

namespace AudioReplayBuffer.Core;

/// <summary>
/// The application's brain, shared by the window and the tray icon: owns
/// the ring buffer, capture engine, saver and global hotkey, and exposes
/// the operations the UI surfaces trigger.
/// </summary>
public sealed class AppController : IDisposable
{
    private const int MainHotkeyId = 1;
    private const int ClipHotkeyId = 2;
    private const int LauncherHotkeyId = 3;
    private const int SlotHotkeyBase = 10; // slots 1..9 → ids 11..19
    private const int StopSoundHotkeyId = 20;

    private readonly object _applyLock = new();
    private readonly HotkeyManager _hotkeys;
    private CircularAudioBuffer _ring;
    private WaveformHistory _waveform;
    private AudioCaptureEngine _engine;
    private ReplaySaver _saver;
    private int _saving;

    public AppSettings Settings { get; private set; }

    /// <summary>Plays replays into the virtual mic; owned here so soundboard hotkeys work without any window.</summary>
    public VoicePlayer Voice { get; } = new();

    /// <summary>Labels and soundboard slot assignments.</summary>
    public SoundboardStore Soundboard { get; } = new();

    /// <summary>Raised from a background thread after a successful save.</summary>
    public event Action<string, TimeSpan>? ReplaySaved;
    public event Action<string>? SaveFailed;
    public event Action<string>? SoundboardError;
    public event Action? StateChanged;

    /// <summary>Raised on the UI thread when the quick-launcher hotkey fires.</summary>
    public event Action? LauncherRequested;

    /// <summary>Folder for imported soundboard sounds, kept apart from the replay history.</summary>
    public string SoundLibraryDir => Path.Combine(Settings.ResolveOutputFolder(), "Soundboard");

    public bool IsCapturing => _engine.IsRunning;
    public bool IsSaving => _saving == 1;
    public TimeSpan BufferedDuration => _ring.BufferedDuration;
    public TimeSpan BufferCapacity => TimeSpan.FromMinutes(Settings.BufferMinutes);
    public float CurrentPeak => _engine.CurrentPeak;
    public float[] WaveformSnapshot => _waveform.Snapshot();

    public AppController(AppSettings settings)
    {
        Settings = settings;
        _ring = CreateRing(settings);
        _waveform = new WaveformHistory(settings.BufferMinutes * 60);
        _engine = new AudioCaptureEngine(settings, _ring, _waveform);
        _saver = new ReplaySaver(settings);
        _hotkeys = new HotkeyManager();
        _hotkeys.HotkeyPressed += id =>
        {
            if (id == MainHotkeyId) SaveReplay();
            else if (id == ClipHotkeyId) SaveClip();
            else if (id == LauncherHotkeyId) LauncherRequested?.Invoke();
            else if (id == StopSoundHotkeyId) Voice.Stop();
            else if (id > SlotHotkeyBase && id <= SlotHotkeyBase + SoundboardStore.SlotCount)
                PlaySlot(id - SlotHotkeyBase);
        };
    }

    private static CircularAudioBuffer CreateRing(AppSettings settings) => new(
        settings.BufferMinutes * 60 * AudioCaptureEngine.BytesPerSecond,
        AudioCaptureEngine.BufferFormat.BlockAlign,
        AudioCaptureEngine.BytesPerSecond);

    /// <summary>Registers all hotkeys (save, clip, assigned soundboard slots). Returns errors or "".</summary>
    public string RegisterHotkey()
    {
        var errors = new List<string>();
        if (!_hotkeys.TryRegister(MainHotkeyId, Settings.Hotkey, out string mainError))
            errors.Add(mainError);
        if (!_hotkeys.TryRegister(ClipHotkeyId, Settings.ClipHotkey, out string clipError))
            errors.Add(clipError);
        if (!_hotkeys.TryRegister(LauncherHotkeyId, Settings.LauncherHotkey, out string launcherError))
            errors.Add(launcherError);

        for (int slot = 1; slot <= SoundboardStore.SlotCount; slot++)
        {
            if (Soundboard.PathOfSlot(slot) != null)
            {
                if (!_hotkeys.TryRegister(SlotHotkeyBase + slot, $"Ctrl+Alt+D{slot}", out string slotError))
                    errors.Add(slotError);
            }
            else
            {
                _hotkeys.Unregister(SlotHotkeyBase + slot);
            }
        }

        if (Soundboard.AnySlotAssigned)
        {
            if (!_hotkeys.TryRegister(StopSoundHotkeyId, "Ctrl+Alt+D0", out string stopError))
                errors.Add(stopError);
        }
        else
        {
            _hotkeys.Unregister(StopSoundHotkeyId);
        }

        return string.Join(" ", errors);
    }

    /// <summary>
    /// Plays any sound file into the call, honoring the per-sound volume
    /// and the overlap/interrupt setting. Returns false when playback
    /// could not start (an error event has been raised).
    /// </summary>
    public bool PlaySound(string path)
    {
        if (!File.Exists(path))
        {
            SoundboardError?.Invoke($"\"{Path.GetFileName(path)}\" no longer exists.");
            return false;
        }
        if (string.IsNullOrWhiteSpace(Settings.VoiceDevice))
        {
            SoundboardError?.Invoke(
                "Set the voice output device in Settings first (your Voicemod or VB-CABLE device).");
            return false;
        }
        try
        {
            float gain = Soundboard.GetVolume(path) / 100f;
            Voice.Play(path, Settings.VoiceDevice, Settings.VoiceAlsoSpeakers,
                       gain, Settings.SoundboardOverlap);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Log($"Playback of {path} failed: {ex}");
            SoundboardError?.Invoke(ex.Message);
            return false;
        }
    }

    /// <summary>Plays the sound assigned to a soundboard slot into the call.</summary>
    public void PlaySlot(int slot)
    {
        string? path = Soundboard.PathOfSlot(slot);
        if (path == null)
            return;
        if (PlaySound(path))
            Logger.Log($"Soundboard {slot} → {Path.GetFileName(path)}");
    }

    public void StartCapture()
    {
        _engine.Start();
        StateChanged?.Invoke();
    }

    public void StopCapture()
    {
        _engine.Stop();
        StateChanged?.Invoke();
    }

    /// <summary>Wipes the in-RAM audio buffer; saved files are unaffected.</summary>
    public void ClearBuffer()
    {
        _ring.Clear();
        _waveform.Clear();
        Logger.Log("Buffer cleared.");
        StateChanged?.Invoke();
    }

    public void SaveReplay() => SaveInternal(tail: null);

    /// <summary>Saves only the last ClipSeconds of the buffer.</summary>
    public void SaveClip() => SaveInternal(TimeSpan.FromSeconds(Settings.ClipSeconds));

    private void SaveInternal(TimeSpan? tail)
    {
        // A second trigger while encoding is ignored rather than queued.
        if (Interlocked.Exchange(ref _saving, 1) == 1)
            return;

        var pcm = _ring.Snapshot();
        if (tail is TimeSpan t)
        {
            long want = (long)(t.TotalSeconds * AudioCaptureEngine.BytesPerSecond);
            want -= want % AudioCaptureEngine.BufferFormat.BlockAlign;
            if (pcm.Length > want)
                pcm = pcm[^(int)want..];
        }
        var duration = TimeSpan.FromSeconds((double)pcm.Length / AudioCaptureEngine.BytesPerSecond);
        var saver = _saver;

        Task.Run(() =>
        {
            try
            {
                string path = saver.Save(pcm, tail != null ? "clip" : null);
                Logger.Log($"Saved {Fmt(duration)} of audio to {path}");
                ReplaySaved?.Invoke(path, duration);
            }
            catch (Exception ex)
            {
                Logger.Log("Save failed: " + ex);
                SaveFailed?.Invoke(ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _saving, 0);
            }
        });
    }

    /// <summary>
    /// Validates, persists and applies new settings live: the capture
    /// engine is rebuilt, the ring buffer only if its size changed (so
    /// already-captured audio survives most edits). Returns a warning
    /// string if the hotkey could not be registered, otherwise "".
    /// </summary>
    public string ApplySettings(AppSettings newSettings)
    {
        lock (_applyLock)
        {
            newSettings.Validate();
            newSettings.Save();

            bool ringChanged = newSettings.BufferMinutes != Settings.BufferMinutes;
            bool wasRunning = _engine.IsRunning;

            _engine.Dispose();
            Settings = newSettings;
            if (ringChanged)
            {
                _ring = CreateRing(newSettings);
                _waveform = new WaveformHistory(newSettings.BufferMinutes * 60);
            }
            _engine = new AudioCaptureEngine(newSettings, _ring, _waveform);
            _saver = new ReplaySaver(newSettings);
            if (wasRunning)
                _engine.Start();

            string warning = RegisterHotkey();
            Logger.Log("Settings applied." + (warning.Length > 0 ? " Warning: " + warning : ""));
            StateChanged?.Invoke();
            return warning;
        }
    }

    public static string Fmt(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    public void Dispose()
    {
        _hotkeys.Dispose();
        Voice.Dispose();
        _engine.Dispose();
    }
}
