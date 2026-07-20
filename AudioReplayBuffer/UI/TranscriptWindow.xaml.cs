using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AudioReplayBuffer.Configuration;
using AudioReplayBuffer.Core;
using NAudio.Wave;

namespace AudioReplayBuffer.UI;

/// <summary>
/// Shows the transcript with synchronized local playback: the active line
/// highlights and scrolls with the audio, clicking a line jumps playback
/// to that moment, and (with speaker detection) speakers are colored and
/// renamable. A plain .txt is saved next to the audio.
/// </summary>
public partial class TranscriptWindow : Window
{
    private sealed record SegmentVm(TimeSpan Start, TimeSpan End, string Time, string Text,
                                    string? SpeakerKey, string SpeakerDisplay,
                                    Brush? SpeakerBrush, Visibility SpeakerVisibility);

    private static readonly Brush[] SpeakerPalette =
    [
        new SolidColorBrush(Color.FromRgb(0x4F, 0x8C, 0xFF)),
        new SolidColorBrush(Color.FromRgb(0x46, 0xC0, 0x66)),
        new SolidColorBrush(Color.FromRgb(0xE5, 0xA3, 0x3B)),
        new SolidColorBrush(Color.FromRgb(0xA0, 0x6C, 0xE5)),
        new SolidColorBrush(Color.FromRgb(0xE5, 0x6C, 0xB3)),
        new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D)),
    ];

    private readonly string _audioPath;
    private readonly AppSettings _settings;
    private readonly CancellationTokenSource _cts = new();
    private readonly DispatcherTimer _timer;
    private TranscriptResult? _result;
    private List<SegmentVm> _segments = [];
    private readonly Dictionary<string, string> _speakerNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Brush> _speakerBrushes = new(StringComparer.OrdinalIgnoreCase);

    private AudioFileReader? _reader;
    private IWavePlayer? _player;
    private bool _syncingSelection;

    public TranscriptWindow(string audioPath, AppSettings settings)
    {
        _audioPath = audioPath;
        _settings = settings;
        InitializeComponent();
        FileNameText.Text = Path.GetFileName(audioPath);
        Title = $"Transcript — {Path.GetFileName(audioPath)}";
        StatusText.Text = settings.DetectSpeakers
            ? "Transcribing with speaker detection — uploading to AssemblyAI…"
            : "Transcribing — uploading to Groq…";

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _timer.Tick += (_, _) => SyncWithPlayback();

        Loaded += async (_, _) => await RunAsync();
        Closed += (_, _) =>
        {
            _cts.Cancel();
            _timer.Stop();
            DisposePlayer();
        };
    }

    // ---------- transcription ----------

    private async Task RunAsync()
    {
        try
        {
            _result = _settings.DetectSpeakers
                ? await Transcriber.TranscribeWithSpeakersAsync(_audioPath, _settings.AssemblyAiApiKey, _cts.Token)
                : await Transcriber.TranscribeAsync(_audioPath, _settings.GroqApiKey, _cts.Token);

            string language = _result.Language is string lang && lang.Length > 0
                ? $" · language: {CultureInfo.CurrentCulture.TextInfo.ToTitleCase(lang)}"
                : "";
            StatusText.Text = "Done" + language + " — click a line to play from there";
            CopyBtn.IsEnabled = true;
            PlayBtn.IsEnabled = true;
            StopBtn.IsEnabled = true;

            if (_result.Segments.Count > 0)
            {
                foreach (var segment in _result.Segments)
                    if (segment.Speaker is string key && !_speakerNames.ContainsKey(key))
                    {
                        _speakerNames[key] = key;
                        _speakerBrushes[key] = SpeakerPalette[_speakerBrushes.Count % SpeakerPalette.Length];
                    }
                RebuildSegments();
                SegmentsList.Visibility = Visibility.Visible;
                TranscriptBox.Visibility = Visibility.Collapsed;
            }
            else
            {
                TranscriptBox.Text = _result.Text.Length > 0 ? _result.Text : "(no speech detected)";
            }

            SaveTxt();
        }
        catch (OperationCanceledException)
        {
            // window closed mid-flight
        }
        catch (Exception ex)
        {
            Logger.Log("Transcription failed: " + ex.Message);
            StatusText.Text = "Failed";
            StatusText.Foreground = (Brush)FindResource("WarnBrush");
            TranscriptBox.Text = ex.Message +
                (_settings.DetectSpeakers
                    ? "\n\nCheck your AssemblyAI API key in Settings → Transcription, and your internet connection."
                    : "\n\nCheck your Groq API key in Settings → Transcription, and your internet connection.");
        }
    }

    private void RebuildSegments()
    {
        if (_result == null)
            return;
        int selected = SegmentsList.SelectedIndex;
        _segments = _result.Segments.Select(s =>
        {
            bool hasSpeaker = s.Speaker != null;
            return new SegmentVm(
                s.Start, s.End, Fmt(s.Start), s.Text,
                s.Speaker,
                hasSpeaker ? _speakerNames[s.Speaker!] + ":" : "",
                hasSpeaker ? _speakerBrushes[s.Speaker!] : null,
                hasSpeaker ? Visibility.Visible : Visibility.Collapsed);
        }).ToList();
        _syncingSelection = true;
        SegmentsList.ItemsSource = _segments;
        SegmentsList.SelectedIndex = selected;
        _syncingSelection = false;
    }

    /// <summary>Transcript as plain text, with speaker names when present.</summary>
    private string ComposeText()
    {
        if (_result == null)
            return "";
        if (_result.Segments.Any(s => s.Speaker != null))
            return string.Join(Environment.NewLine,
                _result.Segments.Select(s =>
                    s.Speaker != null ? $"{_speakerNames[s.Speaker]}: {s.Text}" : s.Text));
        return _result.Text;
    }

    private void SaveTxt()
    {
        try
        {
            string txtPath = Path.ChangeExtension(_audioPath, ".txt");
            File.WriteAllText(txtPath, ComposeText());
            SavedText.Text = $"Saved {Path.GetFileName(txtPath)} next to the audio.";
        }
        catch (Exception ex)
        {
            Logger.Log("Transcript .txt save failed: " + ex.Message);
            SavedText.Text = "Could not save the .txt (transcript is still shown here).";
        }
    }

    /// <summary>Click a speaker chip → rename that speaker everywhere.</summary>
    private void OnSpeakerClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true; // don't let the click select the line and seek
        if ((sender as FrameworkElement)?.DataContext is not SegmentVm segment || segment.SpeakerKey == null)
            return;
        string current = _speakerNames[segment.SpeakerKey];
        var prompt = new InputDialog("Rename speaker", $"Name for {current}", current) { Owner = this };
        prompt.ShowDialog();
        if (prompt.Result is not string name || name.Length == 0)
            return;
        _speakerNames[segment.SpeakerKey] = name;
        RebuildSegments();
        SaveTxt();
    }

    // ---------- playback ----------

    private void EnsurePlayer()
    {
        if (_player != null)
            return;
        _reader = new AudioFileReader(_audioPath);
        var player = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, 200);
        player.Init(_reader);
        player.PlaybackStopped += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            // Natural end: tear down so the next Play starts fresh from 0.
            if (!ReferenceEquals(player, _player))
                return;
            DisposePlayer();
            PlayBtn.Content = "▶  Play";
            _timer.Stop();
            ClearActiveSegment();
        });
        _player = player;
    }

    private void DisposePlayer()
    {
        var player = _player;
        var reader = _reader;
        _player = null;
        _reader = null;
        if (player != null || reader != null)
            Task.Run(() =>
            {
                try { player?.Stop(); } catch { }
                player?.Dispose();
                reader?.Dispose();
            });
    }

    private void OnPlayPauseClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_player != null && _player.PlaybackState == PlaybackState.Playing)
            {
                _player.Pause();
                _timer.Stop();
                PlayBtn.Content = "▶  Play";
                return;
            }
            EnsurePlayer();
            _player!.Play();
            _timer.Start();
            PlayBtn.Content = "⏸  Pause";
        }
        catch (Exception ex)
        {
            Logger.Log("Transcript playback failed: " + ex);
            StatusText.Text = "Playback failed: " + ex.Message;
            StatusText.Foreground = (Brush)FindResource("WarnBrush");
        }
    }

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        DisposePlayer();
        _timer.Stop();
        PlayBtn.Content = "▶  Play";
        PositionText.Text = "";
        ClearActiveSegment();
    }

    /// <summary>Clicking a line starts playback at that segment.</summary>
    private void OnSegmentSelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_syncingSelection || SegmentsList.SelectedItem is not SegmentVm segment)
            return;
        try
        {
            EnsurePlayer();
            _reader!.CurrentTime = segment.Start;
            if (_player!.PlaybackState != PlaybackState.Playing)
            {
                _player.Play();
                _timer.Start();
                PlayBtn.Content = "⏸  Pause";
            }
        }
        catch (Exception ex)
        {
            Logger.Log("Transcript seek failed: " + ex);
        }
    }

    private void SyncWithPlayback()
    {
        if (_reader == null)
            return;
        var t = _reader.CurrentTime;
        PositionText.Text = $"{Fmt(t)} / {Fmt(_reader.TotalTime)}";

        if (_segments.Count == 0)
            return;
        int active = _segments.FindIndex(s => t >= s.Start && t < s.End);
        if (active < 0 || SegmentsList.SelectedIndex == active)
            return;
        _syncingSelection = true;
        SegmentsList.SelectedIndex = active;
        SegmentsList.ScrollIntoView(_segments[active]);
        _syncingSelection = false;
    }

    private void ClearActiveSegment()
    {
        _syncingSelection = true;
        SegmentsList.SelectedIndex = -1;
        _syncingSelection = false;
    }

    // ---------- misc ----------

    private static string Fmt(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_result != null ? ComposeText() : TranscriptBox.Text);
            SavedText.Text = "Copied to clipboard.";
        }
        catch (Exception ex)
        {
            Logger.Log("Clipboard copy failed: " + ex.Message);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        int dark = 1;
        _ = DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int));
    }
}
