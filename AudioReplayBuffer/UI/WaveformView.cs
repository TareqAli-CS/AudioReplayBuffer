using System.Windows;
using System.Windows.Media;

namespace AudioReplayBuffer.UI;

/// <summary>
/// Lightweight waveform display: one vertical bar per pixel column,
/// mirrored around the center line. Redrawn a few times per second from
/// the peak history — no retained visual tree, just OnRender.
/// </summary>
public sealed class WaveformView : FrameworkElement
{
    private static readonly Pen WavePen = MakePen(Color.FromRgb(0x4F, 0x8C, 0xFF), 1.0, 0.9);
    private static readonly Pen CenterPen = MakePen(Color.FromRgb(0x2C, 0x2C, 0x34), 1.0, 1.0);

    private float[] _peaks = [];

    private static Pen MakePen(Color color, double thickness, double opacity)
    {
        var brush = new SolidColorBrush(color) { Opacity = opacity };
        brush.Freeze();
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }

    public void Update(float[] peaks)
    {
        _peaks = peaks;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 2 || h < 2)
            return;

        double mid = h / 2;
        dc.DrawLine(CenterPen, new Point(0, mid), new Point(w, mid));

        var peaks = _peaks;
        if (peaks.Length == 0)
            return;

        int columns = (int)w;
        for (int x = 0; x < columns; x++)
        {
            // Max over the slice of history this pixel column covers, so
            // short loud moments stay visible at any zoom.
            int from = (int)((long)x * peaks.Length / columns);
            int to = (int)((long)(x + 1) * peaks.Length / columns);
            float peak = 0;
            for (int i = from; i < Math.Max(to, from + 1) && i < peaks.Length; i++)
                if (peaks[i] > peak) peak = peaks[i];

            double half = Math.Max(0.6, peak * (mid - 1));
            dc.DrawLine(WavePen, new Point(x + 0.5, mid - half), new Point(x + 0.5, mid + half));
        }
    }
}
