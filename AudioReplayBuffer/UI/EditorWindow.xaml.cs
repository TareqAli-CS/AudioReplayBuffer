using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AudioReplayBuffer.Configuration;
using AudioReplayBuffer.Core;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AudioReplayBuffer.UI;

/// <summary>
/// Editor for a saved replay: the file is decoded to 48 kHz stereo floats
/// held in memory, edits operate on that array (with an undo stack), and
/// saving re-encodes to MP3.
/// </summary>
public partial class EditorWindow : Window
{
    private const int SampleRate = 48000;
    private const int Channels = 2;
    private const int MaxUndoLevels = 3;

    private readonly string _sourcePath;
    private readonly ReplaySaver _saver;
    private readonly DispatcherTimer _playTimer;
    private readonly List<float[]> _undoStack = [];

    private float[] _samples = [];
    private IWavePlayer? _player;
    private ArraySampleProvider? _playProvider;
    private long _playStartFrame;
    private bool _syncingSelection;

    /// <summary>Raised (from a background thread) after a successful export.</summary>
    public event Action? FileSaved;

    public EditorWindow(string filePath, AppSettings settings)
    {
        _sourcePath = filePath;
        _saver = new ReplaySaver(settings);
        InitializeComponent();

        FileNameText.Text = Path.GetFileName(filePath);
        Title = $"Edit — {Path.GetFileName(filePath)}";

        Wave.SelectionChanged += OnWaveSelectionChanged;
        RangeSel.RangeChanged += OnRangeSliderChanged;
        _playTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _playTimer.Tick += (_, _) => UpdatePlayhead();

        Loaded += (_, _) => LoadFile();
        Closing += (_, _) => StopPlayback();
    }

    // ---------- loading ----------

    private void LoadFile()
    {
        try
        {
            using var reader = new AudioFileReader(_sourcePath);
            ISampleProvider sp = reader;
            if (sp.WaveFormat.SampleRate != SampleRate)
                sp = new WdlResamplingSampleProvider(sp, SampleRate);
            if (sp.WaveFormat.Channels == 1)
                sp = new MonoToStereoSampleProvider(sp);
            else if (sp.WaveFormat.Channels > Channels)
                sp = new ChannelDownmixProvider(sp);

            var chunks = new List<float[]>();
            long total = 0;
            var buf = new float[SampleRate * Channels]; // 1 s
            int read;
            while ((read = sp.Read(buf, 0, buf.Length)) > 0)
            {
                chunks.Add(buf[..read]);
                total += read;
                if (total > 400_000_000) // ~35 min — refuse to balloon memory
                    throw new InvalidOperationException("File is too long to edit (over ~35 minutes).");
            }

            _samples = new float[total];
            long offset = 0;
            foreach (var chunk in chunks)
            {
                chunk.CopyTo(_samples, offset);
                offset += chunk.Length;
            }

            Wave.SetSamples(_samples, Channels);
            RangeSel.SetRange(0, TotalFrames, TotalFrames);
            UpdateDurationText();
            UpdateSelectionInfo();
        }
        catch (Exception ex)
        {
            Logger.Log($"Editor could not open {_sourcePath}: {ex}");
            MessageBox.Show("Could not open the file:\n\n" + ex.Message, "Edit replay",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }

    private static string Fmt(long frames)
    {
        double seconds = (double)frames / SampleRate;
        return $"{(int)(seconds / 60)}:{seconds % 60:00.0}";
    }

    private void UpdateDurationText()
        => DurationText.Text = $"{Fmt(_samples.Length / Channels)} · 48 kHz stereo";

    private long TotalFrames => _samples.Length / Channels;

    private void UpdateSelectionInfo()
    {
        SelInfoText.Text = Wave.Selection is (long start, long end)
            ? $"Range: {Fmt(start)} – {Fmt(end)}  ({Fmt(end - start)})"
            : "Drag the handles below the waveform (or drag on the waveform) to choose a range";
    }

    /// <summary>Range slider moved → mirror onto the waveform selection.</summary>
    private void OnRangeSliderChanged()
    {
        if (_syncingSelection)
            return;
        _syncingSelection = true;
        long start = (long)RangeSel.ValueStart;
        long end = (long)RangeSel.ValueEnd;
        // Handles at the extremes mean "whole file" — shown as no selection.
        if (start <= 0 && end >= TotalFrames)
            Wave.ClearSelection();
        else
            Wave.SetSelection(start, end);
        _syncingSelection = false;
        UpdateSelectionInfo();
    }

    /// <summary>Waveform selection changed (drag or buttons) → move the slider handles.</summary>
    private void OnWaveSelectionChanged()
    {
        if (!_syncingSelection)
        {
            _syncingSelection = true;
            if (Wave.Selection is (long start, long end))
                RangeSel.SetRange(start, end, TotalFrames);
            else
                RangeSel.SetRange(0, TotalFrames, TotalFrames);
            _syncingSelection = false;
        }
        UpdateSelectionInfo();
    }

    // ---------- playback ----------

    private void StartPlayback(long startFrame, long endFrame)
    {
        StopPlayback();
        if (_samples.Length == 0 || endFrame <= startFrame)
            return;

        _playStartFrame = startFrame;
        var provider = new ArraySampleProvider(_samples,
            (int)(startFrame * Channels), (int)(endFrame * Channels));
        var player = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, 200);

        try
        {
            player.Init(provider);
            player.Play();
        }
        catch (Exception ex)
        {
            Logger.Log("Editor playback failed: " + ex);
            ShowStatus("Playback failed: " + ex.Message, warn: true);
            player.Dispose();
            return;
        }

        _playProvider = provider;
        _player = player;
        player.PlaybackStopped += (_, args) => Dispatcher.BeginInvoke(() =>
        {
            // Ignore late events from a player that was already replaced;
            // acting on them would kill the playhead of the new playback.
            if (!ReferenceEquals(player, _player))
                return;
            _player = null;
            _playProvider = null;
            _playTimer.Stop();
            Wave.SetPlayhead(-1);
            PlayBtn.Content = "▶  Play";
            player.Dispose();
            if (args.Exception != null)
            {
                Logger.Log("Editor playback error: " + args.Exception);
                ShowStatus("Playback error: " + args.Exception.Message, warn: true);
            }
        });
        _playTimer.Start();
        PlayBtn.Content = "⏸  Pause";
    }

    private void StopPlayback()
    {
        _playTimer.Stop();
        var player = _player;
        _player = null; // cleared first so the PlaybackStopped guard skips it
        _playProvider = null;
        Wave.SetPlayhead(-1);
        PlayBtn.Content = "▶  Play";
        if (player != null)
        {
            try { player.Stop(); } catch { }
            player.Dispose();
        }
    }

    private void UpdatePlayhead()
    {
        if (_playProvider != null)
            Wave.SetPlayhead(_playStartFrame + _playProvider.SamplesServed / Channels);
    }

    private void OnPlayClick(object sender, RoutedEventArgs e)
    {
        // Toggles pause/resume while something is loaded; otherwise starts
        // from the selection start (or the top).
        if (_player != null)
        {
            if (_player.PlaybackState == PlaybackState.Playing)
            {
                _player.Pause();
                _playTimer.Stop();
                PlayBtn.Content = "▶  Play";
            }
            else
            {
                _player.Play();
                _playTimer.Start();
                PlayBtn.Content = "⏸  Pause";
            }
            return;
        }
        StartPlayback(Wave.Selection?.Start ?? 0, _samples.Length / Channels);
    }

    private void OnPlaySelectionClick(object sender, RoutedEventArgs e)
    {
        if (Wave.Selection is (long start, long end))
            StartPlayback(start, end);
        else
            ShowStatus("Select a region first — drag on the waveform.", warn: true);
    }

    private void OnStopClick(object sender, RoutedEventArgs e) => StopPlayback();

    private void OnSelectAllClick(object sender, RoutedEventArgs e) => Wave.SelectAll();

    private void OnClearSelectionClick(object sender, RoutedEventArgs e) => Wave.ClearSelection();

    // ---------- editing ----------

    private void PushUndo()
    {
        if (_undoStack.Count >= MaxUndoLevels)
            _undoStack.RemoveAt(0);
        _undoStack.Add(_samples);
        UndoBtn.IsEnabled = true;
    }

    private void AfterEdit(string status)
    {
        Wave.SetSamples(_samples, Channels);
        Wave.ClearSelection();
        UpdateDurationText();
        ShowStatus(status, warn: false);
    }

    /// <summary>Selection range, or the whole file for tools that allow it.</summary>
    private (long Start, long End) RangeOrAll()
        => Wave.Selection ?? (0, _samples.Length / Channels);

    private void OnTrimClick(object sender, RoutedEventArgs e)
    {
        if (Wave.Selection is not (long start, long end))
        {
            ShowStatus("Select the part to keep first.", warn: true);
            return;
        }
        StopPlayback();
        PushUndo();
        _samples = _samples[(int)(start * Channels)..(int)(end * Channels)];
        AfterEdit($"Trimmed to {Fmt(end - start)}.");
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (Wave.Selection is not (long start, long end))
        {
            ShowStatus("Select the part to delete first.", warn: true);
            return;
        }
        if (end - start >= TotalFrames)
        {
            ShowStatus("That would delete the whole file — narrow the range first.", warn: true);
            return;
        }
        StopPlayback();
        PushUndo();
        var result = new float[_samples.Length - (end - start) * Channels];
        Array.Copy(_samples, 0, result, 0, start * Channels);
        Array.Copy(_samples, end * Channels, result, start * Channels, _samples.Length - end * Channels);
        _samples = result;
        AfterEdit($"Deleted {Fmt(end - start)}.");
    }

    private void OnFadeInClick(object sender, RoutedEventArgs e) => ApplyFade(fadeIn: true);

    private void OnFadeOutClick(object sender, RoutedEventArgs e) => ApplyFade(fadeIn: false);

    private void ApplyFade(bool fadeIn)
    {
        var (start, end) = RangeOrAll();
        if (end <= start)
            return;
        StopPlayback();
        PushUndo();
        _samples = (float[])_samples.Clone();
        long frames = end - start;
        for (long f = start; f < end; f++)
        {
            float factor = (float)(f - start) / frames;
            if (!fadeIn)
                factor = 1f - factor;
            _samples[f * Channels] *= factor;
            _samples[f * Channels + 1] *= factor;
        }
        AfterEdit(fadeIn ? "Fade in applied." : "Fade out applied.");
    }

    private void OnGainSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (GainText != null)
            GainText.Text = $"{(int)GainSlider.Value}%";
    }

    private void OnApplyGainClick(object sender, RoutedEventArgs e)
    {
        var (start, end) = RangeOrAll();
        float gain = (float)(GainSlider.Value / 100.0);
        StopPlayback();
        PushUndo();
        _samples = (float[])_samples.Clone();
        for (long i = start * Channels; i < end * Channels; i++)
            _samples[i] = Math.Clamp(_samples[i] * gain, -1f, 1f);
        AfterEdit($"Volume {(int)GainSlider.Value}% applied.");
    }

    private void OnNormalizeClick(object sender, RoutedEventArgs e)
    {
        if (_samples.Length == 0)
            return;
        float peak = 0;
        foreach (float v in _samples)
        {
            float a = v < 0 ? -v : v;
            if (a > peak) peak = a;
        }
        if (peak < 0.0001f)
        {
            ShowStatus("The audio is silent — nothing to normalize.", warn: true);
            return;
        }
        StopPlayback();
        PushUndo();
        float gain = 0.95f / peak;
        _samples = (float[])_samples.Clone();
        for (long i = 0; i < _samples.Length; i++)
            _samples[i] *= gain;
        AfterEdit($"Normalized (peak was {20 * Math.Log10(peak):0.0} dB).");
    }

    private void OnUndoClick(object sender, RoutedEventArgs e)
    {
        if (_undoStack.Count == 0)
            return;
        StopPlayback();
        _samples = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        UndoBtn.IsEnabled = _undoStack.Count > 0;
        AfterEdit("Undone.");
    }

    // ---------- saving ----------

    /// <summary>16-bit PCM of a frame range of the working samples.</summary>
    private byte[] ToPcm16(long startFrame, long endFrame)
    {
        int from = (int)(startFrame * Channels);
        int to = (int)(endFrame * Channels);
        var bytes = new byte[(to - from) * 2];
        for (int i = from; i < to; i++)
        {
            var s = (short)Math.Clamp((int)(_samples[i] * 32767f), short.MinValue, short.MaxValue);
            bytes[(i - from) * 2] = (byte)s;
            bytes[(i - from) * 2 + 1] = (byte)(s >> 8);
        }
        return bytes;
    }

    /// <summary>
    /// What saving exports: the selected range if the handles enclose one,
    /// otherwise the whole file. This makes "drag handles → save" work
    /// without an explicit trim step.
    /// </summary>
    private (long Start, long End, bool IsSelection) ExportRange()
        => Wave.Selection is (long start, long end)
            ? (start, end, true)
            : (0, TotalFrames, false);

    private void OnSaveCopyClick(object sender, RoutedEventArgs e)
    {
        var (start, end, isSelection) = ExportRange();
        string folder = Path.GetDirectoryName(_sourcePath)!;
        string name = Path.GetFileNameWithoutExtension(_sourcePath);
        string outPath = ReplaySaver.UniquePath(folder, name + "-edited", ".mp3");
        string what = isSelection ? $"selection ({Fmt(end - start)})" : "file";
        Export(start, end, outPath, $"Saved {what} as {Path.GetFileName(outPath)}");
    }

    private void OnOverwriteClick(object sender, RoutedEventArgs e)
    {
        var (start, end, isSelection) = ExportRange();
        // Encode next to the original, then swap, so a failed encode never
        // destroys the file.
        string temp = _sourcePath + ".tmp.mp3";
        string what = isSelection ? $"with the selection ({Fmt(end - start)})" : "";
        Export(start, end, temp, ("Original overwritten " + what).TrimEnd(), () =>
        {
            File.Move(temp, _sourcePath, overwrite: true);
        });
    }

    private void Export(long startFrame, long endFrame, string outPath, string successMessage, Action? afterEncode = null)
    {
        StopPlayback();
        if (_samples.Length == 0 || endFrame <= startFrame)
            return;
        var pcm = ToPcm16(startFrame, endFrame);
        SaveCopyBtn.IsEnabled = OverwriteBtn.IsEnabled = false;
        ShowStatus("Encoding…", warn: false);

        Task.Run(() =>
        {
            try
            {
                _saver.EncodeTo(pcm, outPath);
                afterEncode?.Invoke();
                Logger.Log($"Editor saved {outPath}");
                FileSaved?.Invoke();
                Dispatcher.BeginInvoke(() => ShowStatus(successMessage + " ✓", warn: false));
            }
            catch (Exception ex)
            {
                Logger.Log("Editor save failed: " + ex);
                Dispatcher.BeginInvoke(() => ShowStatus("Save failed: " + ex.Message, warn: true));
            }
            finally
            {
                Dispatcher.BeginInvoke(() => SaveCopyBtn.IsEnabled = OverwriteBtn.IsEnabled = true);
            }
        });
    }

    private void ShowStatus(string text, bool warn)
    {
        EditStatus.Text = text;
        EditStatus.Foreground = (Brush)FindResource(warn ? "WarnBrush" : "GreenBrush");
    }

    // ---------- chrome ----------

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        int dark = 1;
        _ = DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int));
    }

    /// <summary>Plays a slice of an interleaved float array.</summary>
    private sealed class ArraySampleProvider(float[] data, int startSample, int endSample) : ISampleProvider
    {
        private int _pos = startSample;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);

        /// <summary>Samples handed to the output device so far.</summary>
        public int SamplesServed => _pos - startSample;

        public int Read(float[] buffer, int offset, int count)
        {
            int available = Math.Min(count, endSample - _pos);
            if (available <= 0)
                return 0;
            // No Array.Copy here: NAudio's WaveBuffer hands us a byte[]
            // disguised as float[], and Array.Copy rejects that pairing.
            for (int n = 0; n < available; n++)
                buffer[offset + n] = data[_pos + n];
            _pos += available;
            return available;
        }
    }
}
