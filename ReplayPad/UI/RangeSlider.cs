using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ReplayPad.UI;

/// <summary>
/// Two-thumb range slider: drag the start handle in from the left and the
/// end handle in from the right to choose the kept range. Values are in
/// arbitrary units (the editor uses frames).
/// </summary>
public sealed class RangeSlider : FrameworkElement
{
    private static readonly Brush TrackBrush = Frozen(Color.FromRgb(0x2C, 0x2C, 0x34));
    private static readonly Brush FillBrush = Frozen(Color.FromRgb(0xE5, 0x48, 0x4D));
    private static readonly Brush ThumbBrush = Frozen(Color.FromRgb(0xFF, 0xFF, 0xFF));

    private const double ThumbRadius = 7;
    private int _dragging = -1; // 0 = start thumb, 1 = end thumb

    public double Maximum { get; private set; } = 1;
    public double ValueStart { get; private set; }
    public double ValueEnd { get; private set; } = 1;

    /// <summary>Raised while the user drags a handle (not from SetRange).</summary>
    public event Action? RangeChanged;

    public RangeSlider() => Cursor = Cursors.SizeWE;

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>Programmatic update; does not raise RangeChanged.</summary>
    public void SetRange(double start, double end, double maximum)
    {
        Maximum = Math.Max(1, maximum);
        ValueStart = Math.Clamp(start, 0, Maximum);
        ValueEnd = Math.Clamp(end, ValueStart, Maximum);
        InvalidateVisual();
    }

    private double XOf(double value) => value / Maximum * ActualWidth;

    private double ValueAt(double x)
        => Math.Clamp(x / Math.Max(1, ActualWidth) * Maximum, 0, Maximum);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        double x = e.GetPosition(this).X;
        double dStart = Math.Abs(x - XOf(ValueStart));
        double dEnd = Math.Abs(x - XOf(ValueEnd));
        // Nearest thumb wins; on a tie (overlapping thumbs) pick by side.
        _dragging = dStart < dEnd || (dStart == dEnd && x < XOf(ValueStart)) ? 0 : 1;
        CaptureMouse();
        MoveThumb(x);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging >= 0)
            MoveThumb(e.GetPosition(this).X);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_dragging < 0)
            return;
        _dragging = -1;
        ReleaseMouseCapture();
    }

    private void MoveThumb(double x)
    {
        double value = ValueAt(x);
        if (_dragging == 0)
            ValueStart = Math.Min(value, ValueEnd);
        else
            ValueEnd = Math.Max(value, ValueStart);
        InvalidateVisual();
        RangeChanged?.Invoke();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 2 || h < 2)
            return;

        double mid = h / 2;
        dc.DrawRoundedRectangle(TrackBrush, null, new Rect(0, mid - 2, w, 4), 2, 2);

        double x1 = XOf(ValueStart), x2 = XOf(ValueEnd);
        dc.DrawRoundedRectangle(FillBrush, null, new Rect(x1, mid - 2, Math.Max(0, x2 - x1), 4), 2, 2);

        dc.DrawEllipse(ThumbBrush, null, new Point(x1, mid), ThumbRadius, ThumbRadius);
        dc.DrawEllipse(ThumbBrush, null, new Point(x2, mid), ThumbRadius, ThumbRadius);
    }
}
