using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using AudioReplayBuffer.Core;

namespace AudioReplayBuffer.UI;

/// <summary>
/// Runs the transcription and shows the result with a timestamps toggle;
/// a .txt is saved next to the audio file automatically on success.
/// </summary>
public partial class TranscriptWindow : Window
{
    private readonly string _audioPath;
    private readonly string _apiKey;
    private readonly CancellationTokenSource _cts = new();
    private TranscriptResult? _result;

    public TranscriptWindow(string audioPath, string apiKey)
    {
        _audioPath = audioPath;
        _apiKey = apiKey;
        InitializeComponent();
        FileNameText.Text = Path.GetFileName(audioPath);
        Title = $"Transcript — {Path.GetFileName(audioPath)}";
        Loaded += async (_, _) => await RunAsync();
        Closed += (_, _) => _cts.Cancel();
    }

    private async Task RunAsync()
    {
        try
        {
            _result = await Transcriber.TranscribeAsync(_audioPath, _apiKey, _cts.Token);

            string language = _result.Language is string lang && lang.Length > 0
                ? $" · detected language: {CultureInfo.CurrentCulture.TextInfo.ToTitleCase(lang)}"
                : "";
            StatusText.Text = "Done" + language;
            TimestampsCheck.IsEnabled = _result.Segments.Count > 0;
            CopyBtn.IsEnabled = true;
            RenderTranscript();

            // Auto-save the plain transcript next to the audio.
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

    private void RenderTranscript()
    {
        if (_result == null)
            return;
        if (TimestampsCheck.IsChecked == true && _result.Segments.Count > 0)
        {
            TranscriptBox.Text = string.Join(Environment.NewLine,
                _result.Segments.Select(s => $"[{Fmt(s.Start)}]  {s.Text}"));
        }
        else
        {
            TranscriptBox.Text = _result.Text.Length > 0
                ? _result.Text
                : "(no speech detected)";
        }
    }

    private static string Fmt(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    private void OnTimestampsToggled(object sender, RoutedEventArgs e) => RenderTranscript();

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(TranscriptBox.Text);
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
