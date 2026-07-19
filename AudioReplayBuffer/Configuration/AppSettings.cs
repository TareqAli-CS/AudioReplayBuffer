using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudioReplayBuffer.Configuration;

public enum CaptureMode
{
    Desktop,
    Microphone,
    Both
}

public sealed class AppSettings
{
    /// <summary>"Desktop", "Microphone" or "Both".</summary>
    public string CaptureMode { get; set; } = "Desktop";

    /// <summary>How many minutes of audio the hotkey saves.</summary>
    public int BufferMinutes { get; set; } = 5;

    /// <summary>Where MP3s are written. Empty = Music\Replays.</summary>
    public string OutputFolder { get; set; } = "";

    /// <summary>MP3 bitrate in kbps.</summary>
    public int Bitrate { get; set; } = 192;

    /// <summary>Global hotkey, e.g. "Ctrl+Alt+S" or "F9".</summary>
    public string Hotkey { get; set; } = "Ctrl+Alt+S";

    /// <summary>Second hotkey that saves only the last ClipSeconds.</summary>
    public string ClipHotkey { get; set; } = "Ctrl+Alt+D";

    /// <summary>How many seconds the clip hotkey saves.</summary>
    public int ClipSeconds { get; set; } = 30;

    /// <summary>
    /// Process name to capture instead of the whole desktop, e.g. "DDNet"
    /// or "opera". Empty = capture everything. Only applies when desktop
    /// audio is captured.
    /// </summary>
    public string TargetApp { get; set; } = "";

    /// <summary>True: capture everything EXCEPT TargetApp.</summary>
    public bool TargetAppExclude { get; set; } = false;

    /// <summary>
    /// Render device "Play to mic" plays into — a virtual audio cable
    /// (Voicemod, VB-CABLE, …) whose virtual microphone is selected in the
    /// call app. Empty = feature unconfigured.
    /// </summary>
    public string VoiceDevice { get; set; } = "";

    /// <summary>Also mirror "Play to mic" playback to the default speakers.</summary>
    public bool VoiceAlsoSpeakers { get; set; } = true;

    /// <summary>"Play to mic" playback volume in percent (0–200).</summary>
    public int VoiceVolume { get; set; } = 100;

    /// <summary>Saved files are named Prefix_2026-07-17_21-30-00.mp3.</summary>
    public string FileNamePrefix { get; set; } = "Replay";

    /// <summary>Volume multiplier for desktop audio (1.0 = unchanged).</summary>
    public float DesktopGain { get; set; } = 1.0f;

    /// <summary>Volume multiplier for the microphone (1.0 = unchanged).</summary>
    public float MicrophoneGain { get; set; } = 1.0f;

    /// <summary>Show a tray notification when a replay is saved.</summary>
    public bool ShowNotifications { get; set; } = true;

    /// <summary>
    /// Plays inaudible silence so desktop capture stays active even when
    /// nothing else is playing. Costs ~0% CPU; keeps the timeline accurate.
    /// </summary>
    public bool KeepAliveSilence { get; set; } = true;

    /// <summary>Optional path to ffmpeg.exe, used as encoder fallback.</summary>
    public string FFmpegPath { get; set; } = "";

    [JsonIgnore]
    public CaptureMode Mode { get; private set; } = Configuration.CaptureMode.Desktop;

    public static AppSettings Load()
    {
        string path = Core.AppPaths.SettingsPath;
        if (!File.Exists(path))
            File.WriteAllText(path, DefaultJson);

        var options = new JsonSerializerOptions
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            PropertyNameCaseInsensitive = true
        };

        var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), options) ?? new AppSettings();
        settings.Validate();
        return settings;
    }

    public void Save()
    {
        File.WriteAllText(Core.AppPaths.SettingsPath,
            JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public void Validate()
    {
        if (!Enum.TryParse<CaptureMode>(CaptureMode, ignoreCase: true, out var mode))
            throw new InvalidOperationException(
                $"Invalid CaptureMode \"{CaptureMode}\" in appsettings.json. Use \"Desktop\", \"Microphone\" or \"Both\".");
        Mode = mode;

        BufferMinutes = Math.Clamp(BufferMinutes, 1, 120);
        ClipSeconds = Math.Clamp(ClipSeconds, 5, 600);
        VoiceVolume = Math.Clamp(VoiceVolume, 0, 200);
        Bitrate = Math.Clamp(Bitrate, 64, 320);
        DesktopGain = Math.Clamp(DesktopGain, 0f, 4f);
        MicrophoneGain = Math.Clamp(MicrophoneGain, 0f, 4f);
        if (string.IsNullOrWhiteSpace(FileNamePrefix))
            FileNamePrefix = "Replay";
    }

    public string ResolveOutputFolder()
    {
        if (!string.IsNullOrWhiteSpace(OutputFolder))
            return OutputFolder;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Replays");
    }

    private const string DefaultJson =
        """
        {
          // What to record: "Desktop" (everything the PC plays),
          // "Microphone" (mic only) or "Both" (mixed together).
          "CaptureMode": "Desktop",

          // How many minutes back the hotkey saves.
          "BufferMinutes": 5,

          // Where MP3s are saved. Empty = your Music\Replays folder.
          "OutputFolder": "",

          // MP3 quality in kbps (64 - 320).
          "Bitrate": 192,

          // Global hotkey that saves the replay. Examples: "Ctrl+Alt+S", "F9", "Ctrl+Shift+R".
          "Hotkey": "Ctrl+Alt+S",

          // Second hotkey that saves only the last ClipSeconds seconds.
          "ClipHotkey": "Ctrl+Alt+D",
          "ClipSeconds": 30,

          // Capture only this app's audio (process name, e.g. "opera"). Empty = all apps.
          // TargetAppExclude true = capture everything EXCEPT that app.
          "TargetApp": "",
          "TargetAppExclude": false,

          // "Play to mic": render device of your virtual audio cable (Voicemod,
          // VB-CABLE...). Select your call app's input as that cable's virtual mic.
          "VoiceDevice": "",
          "VoiceAlsoSpeakers": true,
          "VoiceVolume": 100,

          // Files are named Prefix_2026-07-17_21-30-00.mp3
          "FileNamePrefix": "Replay",

          // Volume multipliers (1.0 = unchanged). Only used for the sources you capture.
          "DesktopGain": 1.0,
          "MicrophoneGain": 1.0,

          // Show a tray notification when a replay is saved.
          "ShowNotifications": true,

          // Keeps desktop capture alive during total silence. Leave true unless it causes issues.
          "KeepAliveSilence": true,

          // Optional: full path to ffmpeg.exe, used as a fallback MP3 encoder.
          "FFmpegPath": ""
        }
        """;
}
