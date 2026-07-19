using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AudioReplayBuffer.Configuration;
using AudioReplayBuffer.Core;
using CaptureMode = AudioReplayBuffer.Configuration.CaptureMode;

namespace AudioReplayBuffer.UI;

public partial class MainWindow : Window
{
    private readonly AppController _controller;
    private readonly VoicePlayer _voicePlayer;
    private readonly DispatcherTimer _uiTimer;
    private readonly DispatcherTimer _volumeSaveTimer;
    private double _displayedLevel;
    private int _waveTicks;
    private bool _loadingUi;
    private readonly Dictionary<string, (DateTime Stamp, long Size, TimeSpan Duration)> _durationCache =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed record RecentItem(string Name, string Details, string FullPath);

    public MainWindow(AppController controller)
    {
        _controller = controller;
        _voicePlayer = controller.Voice;
        InitializeComponent();

        _loadingUi = true;
        VoiceVolumeSlider.Value = controller.Settings.VoiceVolume;
        MirrorCheck.IsChecked = controller.Settings.VoiceAlsoSpeakers;
        _loadingUi = false;
        VoiceVolumeText.Text = $"{controller.Settings.VoiceVolume}%";
        _voicePlayer.SetVolume(controller.Settings.VoiceVolume / 100f);
        MirrorCheck.Checked += OnMirrorToggled;
        MirrorCheck.Unchecked += OnMirrorToggled;

        UpdateStaticTexts();
        RefreshRecentList();

        _controller.ReplaySaved += (path, duration) => Dispatcher.BeginInvoke(() =>
        {
            ShowSaveStatus($"Saved {Path.GetFileName(path)} ({AppController.Fmt(duration)})", ok: true);
            RefreshRecentList();
        });
        _controller.SaveFailed += message => Dispatcher.BeginInvoke(() =>
            ShowSaveStatus("Save failed: " + message, ok: false));
        _controller.SoundboardError += message => Dispatcher.BeginInvoke(() =>
            ShowSaveStatus(message, ok: false));
        _voicePlayer.PlaybackEnded += () => Dispatcher.BeginInvoke(() => MicPlayBtn.Content = "Play to mic");

        // Persisting the volume on every slider tick would hammer the disk;
        // save it shortly after the user stops moving it.
        _volumeSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _volumeSaveTimer.Tick += (_, _) =>
        {
            _volumeSaveTimer.Stop();
            try { _controller.Settings.Save(); } catch (Exception ex) { Logger.Log("Volume save failed: " + ex.Message); }
        };

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _uiTimer.Tick += (_, _) => UpdateLiveState();
        _uiTimer.Start();

        // Catch files that appeared while the window wasn't in focus
        // (standalone editor saves, manual folder changes, …).
        Activated += (_, _) => RefreshRecentList();
    }

    // ---------- live status ----------

    private void UpdateLiveState()
    {
        bool running = _controller.IsCapturing;

        StatusDot.Fill = (Brush)FindResource(running ? "AccentBrush" : "DimBrush");
        StatusText.Text = running ? "Recording" : "Paused";
        PauseBtn.Content = running ? "Pause" : "Resume";

        double peak = running ? _controller.CurrentPeak * 100 : 0;
        _displayedLevel = Math.Max(peak, _displayedLevel * 0.85);
        LevelBar.Value = Math.Min(100, _displayedLevel);

        var buffered = _controller.BufferedDuration;
        var capacity = _controller.BufferCapacity;
        BufferBar.Maximum = Math.Max(1, capacity.TotalSeconds);
        BufferBar.Value = Math.Min(buffered.TotalSeconds, capacity.TotalSeconds);
        BufferedText.Text = $"{AppController.Fmt(buffered)} / {AppController.Fmt(capacity)}";

        // The waveform only needs a few redraws per second.
        if (++_waveTicks >= 3 && IsVisible)
        {
            _waveTicks = 0;
            WaveView.Update(_controller.WaveformSnapshot);
        }
    }

    private void UpdateStaticTexts()
    {
        var s = _controller.Settings;
        string source = s.Mode switch
        {
            CaptureMode.Desktop => "Desktop audio",
            CaptureMode.Microphone => "Microphone",
            _ => "Desktop + microphone"
        };
        if (s.Mode != CaptureMode.Microphone && !string.IsNullOrWhiteSpace(s.TargetApp))
            source = s.TargetAppExclude ? $"Everything except {s.TargetApp}" : $"{s.TargetApp} only";
        StatusSub.Text = $"{source} — keeping the last {s.BufferMinutes} min";
        HotkeyHint.Text = $"or press {s.Hotkey} anywhere";
        ClipBtn.Content = $"Save last {s.ClipSeconds} s  ({s.ClipHotkey})";
    }

    private void OnPauseClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_controller.IsCapturing)
                _controller.StopCapture();
            else
                _controller.StartCapture();
        }
        catch (Exception ex)
        {
            Logger.Log("Pause/resume failed: " + ex);
            ShowSaveStatus(ex.Message, ok: false);
        }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_controller) { Owner = this };
        dialog.ShowDialog();
        if (dialog.Saved)
        {
            UpdateStaticTexts();
            RefreshRecentList();
            _loadingUi = true;
            MirrorCheck.IsChecked = _controller.Settings.VoiceAlsoSpeakers;
            _loadingUi = false;
        }
    }

    // ---------- saving ----------

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        ShowSaveStatus("Encoding…", ok: true);
        _controller.SaveReplay();
    }

    private void OnClipClick(object sender, RoutedEventArgs e)
    {
        ShowSaveStatus("Encoding clip…", ok: true);
        _controller.SaveClip();
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        _controller.ClearBuffer();
        WaveView.Update([]);
        ShowSaveStatus("Buffer cleared — recording continues from now", ok: true);
    }

    private void ShowSaveStatus(string text, bool ok)
    {
        SaveStatus.Text = text;
        SaveStatus.Foreground = (Brush)FindResource(ok ? "GreenBrush" : "WarnBrush");
        SaveStatus.Visibility = Visibility.Visible;
    }

    // ---------- recent replays ----------

    private void RefreshRecentList()
    {
        var items = new List<RecentItem>();
        try
        {
            string folder = _controller.Settings.ResolveOutputFolder();
            var store = _controller.Soundboard;
            if (Directory.Exists(folder))
            {
                items = new DirectoryInfo(folder)
                    .EnumerateFiles()
                    .Where(f => f.Extension is ".mp3" or ".wav")
                    .OrderByDescending(f => f.LastWriteTime)
                    .Take(12)
                    .Select(f =>
                    {
                        string? label = store.GetLabel(f.FullName);
                        int? slot = store.SlotOf(f.FullName);
                        TimeSpan? duration = GetDuration(f);
                        string details = $"{f.Length / 1024.0 / 1024.0:0.0} MB · {f.LastWriteTime:ddd HH:mm}";
                        if (duration is TimeSpan d)
                            details = $"{AppController.Fmt(d)} · " + details;
                        if (label != null)
                            details = f.Name + " · " + details;
                        if (slot != null)
                            details += $"  ·  ⌨ Ctrl+Alt+{slot}";
                        return new RecentItem(label ?? f.Name, details, f.FullName);
                    })
                    .ToList();
            }
        }
        catch (Exception ex)
        {
            Logger.Log("Could not list recent replays: " + ex.Message);
        }

        RecentList.ItemsSource = items;
        NoRecentText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Audio duration via Media Foundation, cached by (path, size, mtime)
    /// so the frequent list refreshes don't reopen every file.
    /// </summary>
    private TimeSpan? GetDuration(FileInfo f)
    {
        if (_durationCache.TryGetValue(f.FullName, out var cached) &&
            cached.Stamp == f.LastWriteTime && cached.Size == f.Length)
            return cached.Duration;
        try
        {
            using var reader = new NAudio.Wave.MediaFoundationReader(f.FullName);
            var duration = reader.TotalTime;
            _durationCache[f.FullName] = (f.LastWriteTime, f.Length, duration);
            return duration;
        }
        catch (Exception ex)
        {
            Logger.Log($"Could not read duration of {f.Name}: {ex.Message}");
            return null;
        }
    }

    private void OnRecentDoubleClick(object sender, MouseButtonEventArgs e)
        => PlaySelectedToMic(toggle: false); // double-click retriggers, soundboard-style

    /// <summary>Selects the item under the cursor before the context menu opens.</summary>
    private void OnRecentPreviewRightClick(object sender, MouseButtonEventArgs e)
    {
        var element = e.OriginalSource as DependencyObject;
        while (element != null && element is not System.Windows.Controls.ListBoxItem)
            element = System.Windows.Media.VisualTreeHelper.GetParent(element);
        if (element is System.Windows.Controls.ListBoxItem item)
            item.IsSelected = true;
    }

    private void OnRecentMenuOpening(object sender, System.Windows.Controls.ContextMenuEventArgs e)
    {
        if (RecentList.SelectedItem == null)
            e.Handled = true; // right-clicked empty space — nothing to act on
    }

    private void OnPlayLocalClick(object sender, RoutedEventArgs e)
    {
        if (RecentList.SelectedItem is RecentItem item && File.Exists(item.FullPath))
            Process.Start(new ProcessStartInfo(item.FullPath) { UseShellExecute = true });
    }

    private void OnShowInExplorerClick(object sender, RoutedEventArgs e)
    {
        if (RecentList.SelectedItem is not RecentItem item || !File.Exists(item.FullPath))
        {
            ShowSaveStatus("Select a replay in the list first.", ok: false);
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.FullPath}\"") { UseShellExecute = true });
    }

    private void OnEditClick(object sender, RoutedEventArgs e)
    {
        if (RecentList.SelectedItem is not RecentItem item || !File.Exists(item.FullPath))
        {
            ShowSaveStatus("Select a replay in the list first, then press Edit.", ok: false);
            return;
        }
        var editor = new EditorWindow(item.FullPath, _controller.Settings) { Owner = this };
        // Edited copies must show up in this list right away so they can be
        // played to the mic without reopening the app.
        editor.FileSaved += () => Dispatcher.BeginInvoke(RefreshRecentList);
        editor.Show();
    }

    private void OnRenameClick(object sender, RoutedEventArgs e)
    {
        if (RecentList.SelectedItem is not RecentItem item || !File.Exists(item.FullPath))
        {
            ShowSaveStatus("Select a replay in the list first.", ok: false);
            return;
        }

        var store = _controller.Soundboard;
        string extension = Path.GetExtension(item.FullPath);
        var dialog = new RenameDialog(
            store.GetLabel(item.FullPath) ?? "",
            Path.GetFileNameWithoutExtension(item.FullPath),
            extension,
            store.SlotOf(item.FullPath),
            store.PathOfSlot)
        { Owner = this };
        dialog.ShowDialog();
        if (!dialog.Saved)
            return;

        string path = item.FullPath;
        try
        {
            if (!string.Equals(dialog.FileNameResult, Path.GetFileNameWithoutExtension(path), StringComparison.Ordinal))
            {
                string newPath = Path.Combine(Path.GetDirectoryName(path)!, dialog.FileNameResult + extension);
                if (File.Exists(newPath))
                {
                    ShowSaveStatus($"A file named \"{dialog.FileNameResult}{extension}\" already exists.", ok: false);
                    return;
                }
                File.Move(path, newPath);
                store.RenameFile(path, newPath);
                path = newPath;
            }

            store.SetLabel(path, dialog.LabelResult);
            store.AssignSlot(dialog.SlotResult, path);
            string warning = _controller.RegisterHotkey();
            RefreshRecentList();
            ShowSaveStatus(warning.Length > 0 ? "Saved, but: " + warning : "Saved.", ok: warning.Length == 0);
        }
        catch (Exception ex)
        {
            Logger.Log("Rename failed: " + ex);
            ShowSaveStatus(ex.Message, ok: false);
        }
    }

    private void OnDeleteReplayClick(object sender, RoutedEventArgs e)
    {
        if (RecentList.SelectedItem is not RecentItem item || !File.Exists(item.FullPath))
        {
            ShowSaveStatus("Select a replay in the list first.", ok: false);
            return;
        }

        try
        {
            // If this exact file is streaming into a call, release it first.
            if (_voicePlayer.IsPlaying)
            {
                _voicePlayer.Stop();
                MicPlayBtn.Content = "Play to mic";
            }
            RecycleBin.Delete(item.FullPath);
            _controller.Soundboard.RemoveFile(item.FullPath);
            _controller.RegisterHotkey();
            RefreshRecentList();
            ShowSaveStatus($"{item.Name} moved to the Recycle Bin.", ok: true);
        }
        catch (Exception ex)
        {
            Logger.Log("Delete replay failed: " + ex);
            ShowSaveStatus(ex.Message, ok: false);
        }
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        string folder = _controller.Settings.ResolveOutputFolder();
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowSaveStatus("Could not open folder: " + ex.Message, ok: false);
        }
    }

    // ---------- play to mic ----------

    private void OnPlayToMicClick(object sender, RoutedEventArgs e)
        => PlaySelectedToMic(toggle: true);

    /// <param name="toggle">
    /// True (button): a second press stops playback. False (double-click):
    /// always restart with the chosen replay, like a soundboard pad.
    /// </param>
    private void PlaySelectedToMic(bool toggle)
    {
        if (toggle && _voicePlayer.IsPlaying)
        {
            _voicePlayer.Stop();
            MicPlayBtn.Content = "Play to mic";
            return;
        }

        if (RecentList.SelectedItem is not RecentItem item || !File.Exists(item.FullPath))
        {
            ShowSaveStatus("Select a replay in the list first.", ok: false);
            return;
        }

        var s = _controller.Settings;
        if (string.IsNullOrWhiteSpace(s.VoiceDevice))
        {
            ShowSaveStatus(
                "Set \"Voice output device\" in Settings first (your Voicemod or VB-CABLE device), " +
                "and select its virtual mic as the input in Discord.", ok: false);
            return;
        }

        try
        {
            _voicePlayer.Play(item.FullPath, s.VoiceDevice, s.VoiceAlsoSpeakers);
            MicPlayBtn.Content = "■ Stop mic";
            ShowSaveStatus($"Playing {item.Name} into your call…", ok: true);
        }
        catch (Exception ex)
        {
            Logger.Log("Play to mic failed: " + ex);
            ShowSaveStatus(ex.Message, ok: false);
        }
    }

    private void OnMirrorToggled(object sender, RoutedEventArgs e)
    {
        if (_loadingUi)
            return;
        bool enabled = MirrorCheck.IsChecked == true;
        _controller.Settings.VoiceAlsoSpeakers = enabled;
        _volumeSaveTimer.Stop();
        _volumeSaveTimer.Start();

        if (!enabled && _voicePlayer.IsPlaying)
        {
            // Cut the speakers copy immediately — this is the echo escape hatch.
            _voicePlayer.StopMirror();
            ShowSaveStatus("Speaker copy silenced — your call still hears it.", ok: true);
        }
        else if (enabled && _voicePlayer.IsPlaying)
        {
            ShowSaveStatus("Speakers will join from the next playback.", ok: true);
        }
    }

    private void OnVoiceVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (VoiceVolumeText == null || _loadingUi)
            return;
        int volume = (int)VoiceVolumeSlider.Value;
        VoiceVolumeText.Text = $"{volume}%";
        _controller.Settings.VoiceVolume = volume;
        _voicePlayer.SetVolume(volume / 100f);
        _volumeSaveTimer.Stop();
        _volumeSaveTimer.Start();
    }

    // ---------- window chrome ----------

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Closing hides to tray; the app exits from the tray menu.
        e.Cancel = true;
        Hide();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        int dark = 1;
        _ = DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int));
    }
}
