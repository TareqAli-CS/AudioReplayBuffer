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

    private sealed record SoundPad(string Title, string Sub, string FullPath, bool IsPlaying);

    /// <summary>Selected category chip; null = All.</summary>
    private string? _selectedCategory;

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
        RefreshSoundboard(); // soundboard is the home tab
        UpdateTabVisuals();

        _controller.ReplaySaved += (path, duration) => Dispatcher.BeginInvoke(() =>
        {
            ShowSaveStatus($"Saved {Path.GetFileName(path)} ({AppController.Fmt(duration)})", ok: true);
            RefreshRecentList();
        });
        _controller.SaveFailed += message => Dispatcher.BeginInvoke(() =>
            ShowSaveStatus("Save failed: " + message, ok: false));
        _controller.SoundboardError += message => Dispatcher.BeginInvoke(() =>
            ShowSaveStatus(message, ok: false));
        _voicePlayer.PlaybackEnded += () => Dispatcher.BeginInvoke(() =>
        {
            MicPlayBtn.Content = "Play to mic";
            RefreshSoundboard();
        });

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
        StatusText.Text = running ? "Recording" : "Stopped";
        PauseBtn.Content = running ? "■ Stop" : "▶ Start";
        RailDot.Fill = StatusDot.Fill;
        RailDot.ToolTip = running
            ? $"Recording — {AppController.Fmt(_controller.BufferedDuration)} buffered"
            : "Stopped — press Start in the Replays view";

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
        ShowSoundProperties(item.FullPath);
    }

    /// <summary>Label / rename / category / hotkey / volume dialog, shared by replays and pads.</summary>
    private void ShowSoundProperties(string path)
    {
        var store = _controller.Soundboard;
        string extension = Path.GetExtension(path);
        string lib = _controller.SoundLibraryDir;

        // Categories only apply to sounds inside the soundboard library.
        string parentDir = Path.GetDirectoryName(path)!;
        bool isLibrarySound = Path.GetFullPath(path).StartsWith(
            Path.GetFullPath(lib) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        string? currentCategory = isLibrarySound &&
            !string.Equals(Path.GetFullPath(parentDir), Path.GetFullPath(lib), StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileName(parentDir)
                : null;

        var dialog = new RenameDialog(
            store.GetLabel(path) ?? "",
            Path.GetFileNameWithoutExtension(path),
            extension,
            store.SlotOf(path),
            store.PathOfSlot,
            store.GetVolume(path),
            isLibrarySound ? GetCategories() : null,
            currentCategory)
        { Owner = this };
        dialog.ShowDialog();
        if (!dialog.Saved)
            return;

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

            if (isLibrarySound &&
                !string.Equals(dialog.CategoryResult, currentCategory, StringComparison.OrdinalIgnoreCase))
            {
                string targetDir = dialog.CategoryResult == null ? lib : Path.Combine(lib, dialog.CategoryResult);
                Directory.CreateDirectory(targetDir);
                string movedPath = ReplaySaver.UniquePath(targetDir, Path.GetFileNameWithoutExtension(path), extension);
                File.Move(path, movedPath);
                store.RenameFile(path, movedPath);
                path = movedPath;
            }

            store.SetLabel(path, dialog.LabelResult);
            store.AssignSlot(dialog.SlotResult, path);
            store.SetVolume(path, dialog.VolumeResult);
            string warning = _controller.RegisterHotkey();
            RefreshRecentList();
            RefreshSoundboard();
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

    // ---------- soundboard panel ----------

    private void OnReplaysTabClick(object sender, RoutedEventArgs e)
    {
        ReplayView.Visibility = Visibility.Visible;
        SoundboardView.Visibility = Visibility.Collapsed;
        UpdateTabVisuals();
        RefreshRecentList();
    }

    private void OnSoundboardTabClick(object sender, RoutedEventArgs e)
    {
        ReplayView.Visibility = Visibility.Collapsed;
        SoundboardView.Visibility = Visibility.Visible;
        UpdateTabVisuals();
        RefreshSoundboard();
    }

    private void UpdateTabVisuals()
    {
        bool soundboard = SoundboardView.Visibility == Visibility.Visible;
        ReplaysTabBtn.Opacity = soundboard ? 0.45 : 1.0;
        SoundboardTabBtn.Opacity = soundboard ? 1.0 : 0.45;
    }

    /// <summary>Category subfolder names inside the soundboard library.</summary>
    private List<string> GetCategories()
    {
        try
        {
            string lib = _controller.SoundLibraryDir;
            if (Directory.Exists(lib))
                return Directory.GetDirectories(lib)
                    .Select(Path.GetFileName)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Select(n => n!)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();
        }
        catch (Exception ex)
        {
            Logger.Log("Could not list categories: " + ex.Message);
        }
        return [];
    }

    private void RebuildCategoryChips(List<string> categories)
    {
        if (_selectedCategory != null && !categories.Contains(_selectedCategory, StringComparer.OrdinalIgnoreCase))
            _selectedCategory = null;

        CategoryChips.Children.Clear();
        AddCategoryChip("All", null, categories.Count > 0);
        foreach (string category in categories)
            AddCategoryChip(category, category, true);

        var newBtn = new System.Windows.Controls.Button
        {
            Style = (Style)FindResource("Btn"),
            Content = "＋ Category",
            FontSize = 11,
            Padding = new Thickness(9, 4, 9, 4),
            Margin = new Thickness(0, 0, 6, 6),
            Opacity = 0.7,
            ToolTip = "Create a new category (a subfolder of the soundboard library)"
        };
        newBtn.Click += OnNewCategoryClick;
        CategoryChips.Children.Add(newBtn);
    }

    private void AddCategoryChip(string text, string? value, bool visible)
    {
        if (!visible && value == null)
            return; // hide "All" when there are no categories yet
        bool selected = string.Equals(_selectedCategory, value, StringComparison.OrdinalIgnoreCase) ||
                        (_selectedCategory == null && value == null);
        var chip = new System.Windows.Controls.Button
        {
            Style = (Style)FindResource("Btn"),
            Content = text,
            FontSize = 11,
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 6, 6),
            Opacity = selected ? 1.0 : 0.5,
            FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal
        };
        chip.Click += (_, _) =>
        {
            _selectedCategory = value;
            RefreshSoundboard();
        };
        CategoryChips.Children.Add(chip);
    }

    private void OnNewCategoryClick(object sender, RoutedEventArgs e)
    {
        var prompt = new InputDialog("New category", "Category name (e.g. memes, music)") { Owner = this };
        prompt.ShowDialog();
        if (prompt.Result is not string name || name.Length == 0)
            return;
        try
        {
            Directory.CreateDirectory(Path.Combine(_controller.SoundLibraryDir, name));
            _selectedCategory = name;
            RefreshSoundboard();
        }
        catch (Exception ex)
        {
            Logger.Log("Create category failed: " + ex);
            ShowSaveStatus("Could not create category: " + ex.Message, ok: false);
        }
    }

    /// <summary>Rebuilds the category chips and pad grid from the library folder.</summary>
    private void RefreshSoundboard()
    {
        if (SoundboardView.Visibility != Visibility.Visible)
            return;

        var store = _controller.Soundboard;
        string query = PadSearchBox.Text.Trim();
        string lib = _controller.SoundLibraryDir;
        var categories = GetCategories();
        RebuildCategoryChips(categories);

        var pads = new List<SoundPad>();
        try
        {
            if (Directory.Exists(lib))
            {
                IEnumerable<FileInfo> files;
                if (_selectedCategory == null)
                {
                    files = new DirectoryInfo(lib).EnumerateFiles("*", SearchOption.AllDirectories);
                }
                else
                {
                    string dir = Path.Combine(lib, _selectedCategory);
                    files = Directory.Exists(dir) ? new DirectoryInfo(dir).EnumerateFiles() : [];
                }

                pads = files
                    .Where(f => f.Extension is ".mp3" or ".wav")
                    .Select(f =>
                    {
                        string? label = store.GetLabel(f.FullName);
                        string title = label ?? Path.GetFileNameWithoutExtension(f.Name);
                        int? slot = store.SlotOf(f.FullName);
                        TimeSpan? duration = GetDuration(f);
                        string sub = duration is TimeSpan d ? AppController.Fmt(d) : "";
                        if (slot != null)
                            sub += $"  ⌨{slot}";
                        int volume = store.GetVolume(f.FullName);
                        if (volume != 100)
                            sub += $"  {volume}%";
                        // In the All view, show which category a sound lives in.
                        string parent = Path.GetFileName(f.DirectoryName ?? "");
                        if (_selectedCategory == null &&
                            !string.Equals(f.DirectoryName, lib, StringComparison.OrdinalIgnoreCase) &&
                            parent.Length > 0)
                            sub += $"  · {parent}";
                        return new SoundPad(title, sub.Trim(), f.FullName,
                            _voicePlayer.IsPlayingPath(f.FullName));
                    })
                    .Where(p => query.Length == 0 ||
                                p.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                Path.GetFileName(p.FullPath).Contains(query, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(p => p.Title, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }
        catch (Exception ex)
        {
            Logger.Log("Could not list soundboard: " + ex.Message);
        }

        PadsGrid.ItemsSource = pads;
        NoPadsText.Visibility = pads.Count == 0 && query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnPadSearchChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => RefreshSoundboard();

    private void OnPadClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not SoundPad pad)
            return;
        if (_voicePlayer.IsPlayingPath(pad.FullPath))
            _voicePlayer.StopPath(pad.FullPath);
        else
            _controller.PlaySound(pad.FullPath);
        RefreshSoundboard();
    }

    private static SoundPad? PadFromMenuItem(object sender)
        => ((sender as System.Windows.Controls.MenuItem)?.Parent as System.Windows.Controls.ContextMenu)?
            .PlacementTarget is FrameworkElement fe ? fe.Tag as SoundPad : null;

    private void OnPadPlayLocalClick(object sender, RoutedEventArgs e)
    {
        if (PadFromMenuItem(sender) is SoundPad pad && File.Exists(pad.FullPath))
            Process.Start(new ProcessStartInfo(pad.FullPath) { UseShellExecute = true });
    }

    private void OnPadPropertiesClick(object sender, RoutedEventArgs e)
    {
        if (PadFromMenuItem(sender) is SoundPad pad)
            ShowSoundProperties(pad.FullPath);
    }

    private void OnPadEditClick(object sender, RoutedEventArgs e)
    {
        if (PadFromMenuItem(sender) is not SoundPad pad || !File.Exists(pad.FullPath))
            return;
        var editor = new EditorWindow(pad.FullPath, _controller.Settings) { Owner = this };
        editor.FileSaved += () => Dispatcher.BeginInvoke(RefreshSoundboard);
        editor.Show();
    }

    private void OnPadExplorerClick(object sender, RoutedEventArgs e)
    {
        if (PadFromMenuItem(sender) is SoundPad pad && File.Exists(pad.FullPath))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{pad.FullPath}\"") { UseShellExecute = true });
    }

    private void OnPadDeleteClick(object sender, RoutedEventArgs e)
    {
        if (PadFromMenuItem(sender) is not SoundPad pad)
            return;
        try
        {
            _voicePlayer.StopPath(pad.FullPath);
            RecycleBin.Delete(pad.FullPath);
            _controller.Soundboard.RemoveFile(pad.FullPath);
            _controller.RegisterHotkey();
            RefreshSoundboard();
        }
        catch (Exception ex)
        {
            Logger.Log("Delete sound failed: " + ex);
            ShowSaveStatus(ex.Message, ok: false);
        }
    }

    private void OnAddSoundsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Add sounds to the soundboard",
            Filter = "Audio files (*.mp3;*.wav)|*.mp3;*.wav",
            Multiselect = true
        };
        if (dialog.ShowDialog(this) == true)
            ImportSounds(dialog.FileNames);
    }

    private void OnSoundboardDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnSoundboardDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            ImportSounds(files);
    }

    /// <summary>Copies audio files into the library — into the selected category, if one is active.</summary>
    private void ImportSounds(IEnumerable<string> files)
    {
        int imported = 0;
        try
        {
            string dir = _selectedCategory == null
                ? _controller.SoundLibraryDir
                : Path.Combine(_controller.SoundLibraryDir, _selectedCategory);
            Directory.CreateDirectory(dir);
            foreach (string file in files)
            {
                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext is not (".mp3" or ".wav") || !File.Exists(file))
                    continue;
                string target = ReplaySaver.UniquePath(dir, Path.GetFileNameWithoutExtension(file), ext);
                File.Copy(file, target);
                imported++;
            }
        }
        catch (Exception ex)
        {
            Logger.Log("Import failed: " + ex);
            ShowSaveStatus("Import failed: " + ex.Message, ok: false);
        }
        RefreshSoundboard();
        if (imported > 0)
            ShowSaveStatus($"Added {imported} sound{(imported == 1 ? "" : "s")} to the soundboard.", ok: true);
    }

    private void OnSendToSoundboardClick(object sender, RoutedEventArgs e)
    {
        if (RecentList.SelectedItem is not RecentItem item || !File.Exists(item.FullPath))
        {
            ShowSaveStatus("Select a replay in the list first.", ok: false);
            return;
        }
        ImportSounds([item.FullPath]);
        ShowSaveStatus($"{item.Name} added to the soundboard.", ok: true);
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

        if (_controller.PlaySound(item.FullPath))
        {
            MicPlayBtn.Content = "■ Stop mic";
            ShowSaveStatus($"Playing {item.Name} into your call…", ok: true);
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
