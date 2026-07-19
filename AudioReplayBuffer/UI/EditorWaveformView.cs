using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace AudioReplayBuffer.UI;

/// <summary>
/// Interactive waveform for the editor: drag to select a region, shows a
/// playhead during preview. Renders from precomputed peak buckets so
/// redraws stay cheap even for long recordings.
/// </summary>
public sealed class EditorWaveformView : FrameworkElement
{
    private const int BucketCount = 4096;

    private static readonly Brush BgBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x17, 0x17, 0x1B)));
    private static readonly Brush SelectionBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)));
    private static readonly Pen WavePen = FrozenPen(Color.FromRgb(0x4F, 0x8C, 0xFF), 1.0);
    private static readonly Pen SelEdgePen = FrozenPen(Color.FromRgb(0xE5, 0x48, 0x4D), 1.2);
    private static readonly Pen PlayheadPen = FrozenPen(Color.FromRgb(0x46, 0xC0, 0x66), 1.4);
    private static readonly Pen CenterPen = FrozenPen(Color.FromRgb(0x2C, 0x2C, 0x34), 1.0);

    private float[] _buckets = [];
    private long _totalFrames;
    private long _selAnchor = -1, _selMoving = -1;
    private long _playheadFrame = -1;
    private bool _dragging;

    public event Action? SelectionChanged;

    public long TotalFrames => _totalFrames;

    /// <summary>Ordered selection in frames, or null when nothing is selected.</summary>
    public (long Start, long End)? Selection
    {
        get
        {
            if (_selAnchor < 0 || _selMoving < 0 || _selAnchor == _selMoving)
                return null;
            return _selAnchor < _selMoving ? (_selAnchor, _selMoving) : (_selMoving, _selAnchor);
        }
    }

    private static Brush Frozen(SolidColorBrush b) { b.Freeze(); return b; }

    private static Pen FrozenPen(Color c, double thickness)
    {
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }

    public EditorWaveformView()
    {
        Cursor = Cursors.Cross;
        ClipToBounds = true;
    }

    /// <summary>Interleaved stereo float samples; precomputes peak buckets.</summary>
    public void SetSamples(float[] samples, int channels)
    {
        _totalFrames = samples.Length / channels;
        var buckets = new float[BucketCount];
        if (_totalFrames > 0)
        {
            for (int b = 0; b < BucketCount; b++)
            {
                long from = _totalFrames * b / BucketCount * channels;
                long to = _totalFrames * (b + 1) / BucketCount * channels;
                float peak = 0;
                for (long i = from; i < to; i++)
                {
                    float v = samples[i];
                    if (v < 0) v = -v;
                    if (v > peak) peak = v;
                }
                buckets[b] = peak;
            }
        }
        _buckets = buckets;
        InvalidateVisual();
    }

    /// <summary>Programmatic selection (e.g. from the range slider).</summary>
    public void SetSelection(long start, long end)
    {
        _selAnchor = Math.Clamp(start, 0, _totalFrames);
        _selMoving = Math.Clamp(end, 0, _totalFrames);
        InvalidateVisual();
        SelectionChanged?.Invoke();
    }

    public void ClearSelection()
    {
        _selAnchor = _selMoving = -1;
        InvalidateVisual();
        SelectionChanged?.Invoke();
    }

    public void SelectAll()
    {
        _selAnchor = 0;
        _selMoving = _totalFrames;
        InvalidateVisual();
        SelectionChanged?.Invoke();
    }

    public void SetPlayhead(long frame)
    {
        _playheadFrame = frame;
        InvalidateVisual();
    }

    private long FrameAt(double x)
        => _totalFrames == 0 || ActualWidth <= 0
            ? 0
            : Math.Clamp((long)(x / ActualWidth * _totalFrames), 0, _totalFrames);

    private double XAt(long frame)
        => _totalFrames == 0 ? 0 : (double)frame / _totalFrames * ActualWidth;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (_totalFrames == 0)
            return;
        _dragging = true;
        _selAnchor = _selMoving = FrameAt(e.GetPosition(this).X);
        CaptureMouse();
        InvalidateVisual();
        SelectionChanged?.Invoke();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_dragging)
            return;
        _selMoving = FrameAt(e.GetPosition(this).X);
        InvalidateVisual();
        SelectionChanged?.Invoke();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_dragging)
            return;
        _dragging = false;
        ReleaseMouseCapture();
        SelectionChanged?.Invoke();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 2 || h < 2)
            return;

        dc.DrawRoundedRectangle(BgBrush, null, new Rect(0, 0, w, h), 8, 8);
        double mid = h / 2;
        dc.DrawLine(CenterPen, new Point(0, mid), new Point(w, mid));

        if (_totalFrames > 0)
        {
            int columns = (int)w;
            for (int x = 0; x < columns; x++)
            {
                int from = (int)((long)x * BucketCount / columns);
                int to = Math.Max(from + 1, (int)((long)(x + 1) * BucketCount / columns));
                float peak = 0;
                for (int i = from; i < to && i < BucketCount; i++)
                    if (_buckets[i] > peak) peak = _buckets[i];

                double half = Math.Max(0.6, peak * (mid - 3));
                dc.DrawLine(WavePen, new Point(x + 0.5, mid - half), new Point(x + 0.5, mid + half));
            }
        }

        if (Selection is (long start, long end))
        {
            double x1 = XAt(start), x2 = XAt(end);
            dc.DrawRectangle(SelectionBrush, null, new Rect(x1, 0, x2 - x1, h));
            dc.DrawLine(SelEdgePen, new Point(x1, 0), new Point(x1, h));
            dc.DrawLine(SelEdgePen, new Point(x2, 0), new Point(x2, h));
        }

        if (_playheadFrame >= 0)
        {
            double px = XAt(_playheadFrame);
            dc.DrawLine(PlayheadPen, new Point(px, 0), new Point(px, h));
        }
    }
}
