using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ReplayPad.Core;
using NAudio.Wave;

namespace ReplayPad.UI;

/// <summary>
/// Shows the transcript with synchronized local playback: the active line
/// highlights and scrolls with the audio, and clicking a line jumps
/// playback to that moment. A plain .txt is saved next to the audio.
/// </summary>
public partial class TranscriptWindow : Window
{
    private sealed record SegmentVm(TimeSpan Start, TimeSpan End, string Time, string Text);

    private readonly string _audioPath;
    private readonly string _apiKey;
    private readonly CancellationTokenSource _cts = new();
    private readonly DispatcherTimer _timer;
    private TranscriptResult? _result;
    private List<SegmentVm> _segments = [];

    private AudioFileReader? _reader;
    private IWavePlayer? _player;
    private bool _syncingSelection;

    public TranscriptWindow(string audioPath, string apiKey)
    {
        _audioPath = audioPath;
        _apiKey = apiKey;
        InitializeComponent();
        FileNameText.Text = Path.GetFileName(audioPath);
        Title = $"Transcript — {Path.GetFileName(audioPath)}";

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
            _result = await Transcriber.TranscribeAsync(_audioPath, _apiKey, _cts.Token);

            string language = _result.Language is string lang && lang.Length > 0
                ? $" · detected language: {CultureInfo.CurrentCulture.TextInfo.ToTitleCase(lang)}"
                : "";
            StatusText.Text = "Done" + language + " — click a line to play from there";
            CopyBtn.IsEnabled = true;
            PlayBtn.IsEnabled = true;
            StopBtn.IsEnabled = true;

            if (_result.Segments.Count > 0)
            {
                _segments = _result.Segments
                    .Select(s => new SegmentVm(s.Start, s.End, Fmt(s.Start), s.Text))
                    .ToList();
                SegmentsList.ItemsSource = _segments;
                SegmentsList.Visibility = Visibility.Visible;
                TranscriptBox.Visibility = Visibility.Collapsed;
            }
            else
            {
                TranscriptBox.Text = _result.Text.Length > 0 ? _result.Text : "(no speech detected)";
            }

            try
            {
                string txtPath = Path.ChangeExtension(_audioPath, ".txt");
                File.WriteAllText(txtPath, _result.Text);
                SavedText.Text = $"Saved {Path.GetFileName(txtPath)} next to the audio.";
            }
            catch (Exception ex)
            {
                Logger.Log("Transcript .txt save failed: " + ex.Message);
                SavedText.Text = "Could not save the .txt (transcript is still shown here).";
            }
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
                "\n\nCheck your Groq API key in Settings → Transcription, and your internet connection.";
        }
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
            Clipboard.SetText(_result?.Text ?? TranscriptBox.Text);
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
