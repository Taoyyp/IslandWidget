using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace IslandWidget.UI;

/// <summary>
/// Renders the Wisland liquid frosted-glass island shape:
/// - Flush docked edge with concave surface-tension fillets
/// - Hourglass liquid bridge ("拉丝") when dragged away from the docked edge
/// - Retracting droplet ("小水滴") after breaking free from the edge
/// Zero allocations/animations when stationary!
/// </summary>
public sealed class LiquidSurfaceControl : FrameworkElement
{
    private const double K = 0.5523;

    private Brush _fill = Brushes.Black;
    private Pen _rimPen = new(Brushes.Transparent, 1.0);
    private string _edge = "Top"; // Top, Bottom, Left, Right, Free
    private Rect _bodyRect = new(0, 0, 320, 36);
    private double _cornerRadius = 18;
    private double _pullGap;      // Distance pulled away from docked edge (0 = flush)
    private double _neckStrength; // 0..1 liquid bridge strength
    private double _dripRadius;   // >0 when snapped off and droplet is retracting
    private double _anchorAlong;  // Center coordinate along docked edge

    public void UpdateState(
        ThemePalette palette,
        string edge,
        Rect bodyRect,
        double cornerRadius,
        double pullGap = 0,
        double neckStrength = 0,
        double dripRadius = 0,
        double anchorAlong = 0)
    {
        _fill = palette.IslandFill;
        _rimPen = new Pen(palette.RimBrush, 1.15);
        if (_rimPen.CanFreeze) _rimPen.Freeze();
        _edge = edge;
        _bodyRect = bodyRect;
        _cornerRadius = cornerRadius;
        _pullGap = Math.Max(0, pullGap);
        _neckStrength = Math.Clamp(neckStrength, 0, 1);
        _dripRadius = Math.Max(0, dripRadius);
        _anchorAlong = anchorAlong;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (_bodyRect.Width <= 2 || _bodyRect.Height <= 2)
            return;

        // Corner radii: docked edge corners grow from 4 -> cornerRadius as pulled away
        double dockedR = _edge == "Free"
            ? _cornerRadius
            : Lerp(4.0, _cornerRadius, Math.Clamp(_pullGap / 26.0, 0, 1));

        var (tl, tr, br, bl) = _edge switch
        {
            "Top" => (dockedR, dockedR, _cornerRadius, _cornerRadius),
            "Bottom" => (_cornerRadius, _cornerRadius, dockedR, dockedR),
            "Left" => (dockedR, _cornerRadius, _cornerRadius, dockedR),
            "Right" => (_cornerRadius, dockedR, dockedR, _cornerRadius),
            _ => (_cornerRadius, _cornerRadius, _cornerRadius, _cornerRadius)
        };

        Geometry bodyGeo = BuildRoundedRect(_bodyRect, tl, tr, br, bl);
        Geometry combined = bodyGeo;

        if (_edge != "Free" && _neckStrength > 0.02 && _pullGap > 0.5)
        {
            var neckGeo = BuildNeckGeometry();
            combined = Geometry.Combine(combined, neckGeo, GeometryCombineMode.Union, null);
        }
        else if (_edge != "Free" && _dripRadius > 0.5)
        {
            var dripGeo = BuildDripGeometry();
            combined = Geometry.Combine(combined, dripGeo, GeometryCombineMode.Union, null);
        }

        dc.DrawGeometry(_fill, _rimPen, combined);
    }

    private StreamGeometry BuildNeckGeometry()
    {
        bool horiz = _edge is "Top" or "Bottom";
        double line = _edge switch
        {
            "Top" => 0,
            "Bottom" => ActualHeight,
            "Left" => 0,
            "Right" => ActualWidth,
            _ => 0
        };

        Point Map(double u, double v) => _edge switch
        {
            "Top" => new Point(u, line + v),
            "Bottom" => new Point(u, line - v),
            "Left" => new Point(line + v, u),
            _ => new Point(line - v, u)
        };

        double bodyLen = horiz ? _bodyRect.Width : _bodyRect.Height;
        double c = _anchorAlong > 0 ? _anchorAlong : (horiz ? _bodyRect.Left + _bodyRect.Width / 2 : _bodyRect.Top + _bodyRect.Height / 2);
        double half = bodyLen / 2.0;
        double tc = Math.Clamp(_pullGap / 68.0, 0, 1);
        double ts = Math.Sqrt(tc);
        double wz = Lerp(half * 0.65, Math.Clamp(bodyLen * 0.045, 3.0, 8.0), ts) * _neckStrength;
        double fe = 10.0 * (1.0 - 0.25 * tc) * _neckStrength;
        double fb = 9.0 * SmoothStep(tc * 2.5) * _neckStrength;
        double e = wz + fe;
        double b = Math.Min(wz + fb, Math.Max(wz, half - 6.0));
        double ym = Math.Max(_pullGap * 0.48, fe * 0.8);
        double yb = _pullGap + 6.0;

        var sg = new StreamGeometry();
        using var ctx = sg.Open();
        ctx.BeginFigure(Map(c - e, 0), isFilled: true, isClosed: true);
        ctx.LineTo(Map(c + e, 0), isStroked: true, isSmoothJoin: true);
        ctx.BezierTo(Map(c + wz + (e - wz) * (1 - K), 0), Map(c + wz, ym * (1 - K)), Map(c + wz, ym), true, true);
        ctx.BezierTo(Map(c + wz, ym + (yb - ym) * K), Map(c + b - (b - wz) * K, yb), Map(c + b, yb), true, true);
        ctx.LineTo(Map(c - b, yb), isStroked: false, isSmoothJoin: true);
        ctx.BezierTo(Map(c - b + (b - wz) * K, yb), Map(c - wz, ym + (yb - ym) * K), Map(c - wz, ym), true, true);
        ctx.BezierTo(Map(c - wz, ym * (1 - K)), Map(c - wz - (e - wz) * (1 - K), 0), Map(c - e, 0), true, true);
        sg.Freeze();
        return sg;
    }

    private StreamGeometry BuildDripGeometry()
    {
        double line = _edge switch
        {
            "Top" => 0,
            "Bottom" => ActualHeight,
            "Left" => 0,
            "Right" => ActualWidth,
            _ => 0
        };
        Point Map(double u, double v) => _edge switch
        {
            "Top" => new Point(u, line + v),
            "Bottom" => new Point(u, line - v),
            "Left" => new Point(line + v, u),
            _ => new Point(line - v, u)
        };

        double r = _dripRadius;
        double f = r * 0.8;
        double c = _anchorAlong;
        var sg = new StreamGeometry();
        using var ctx = sg.Open();
        ctx.BeginFigure(Map(c - r - f, 0), true, true);
        ctx.LineTo(Map(c + r + f, 0), true, true);
        ctx.BezierTo(Map(c + r + f * (1 - K), 0), Map(c + r, f * (1 - K)), Map(c + r, f * 0.9), true, true);
        ctx.BezierTo(Map(c + r, f + r * 0.9), Map(c - r, f + r * 0.9), Map(c - r, f * 0.9), true, true);
        ctx.BezierTo(Map(c - r, f * (1 - K)), Map(c - r - f * (1 - K), 0), Map(c - r - f, 0), true, true);
        sg.Freeze();
        return sg;
    }

    private static StreamGeometry BuildRoundedRect(Rect rect, double tl, double tr, double br, double bl)
    {
        double maxR = Math.Min(rect.Width, rect.Height) / 2.0;
        tl = Math.Clamp(tl, 0, maxR);
        tr = Math.Clamp(tr, 0, maxR);
        br = Math.Clamp(br, 0, maxR);
        bl = Math.Clamp(bl, 0, maxR);

        double x = rect.X, y = rect.Y, w = rect.Width, h = rect.Height;
        var sg = new StreamGeometry();
        using var ctx = sg.Open();
        ctx.BeginFigure(new Point(x + tl, y), isFilled: true, isClosed: true);
        ctx.LineTo(new Point(x + w - tr, y), true, true);
        if (tr > 0.1)
            ctx.BezierTo(new Point(x + w - tr * (1 - K), y), new Point(x + w, y + tr * (1 - K)), new Point(x + w, y + tr), true, true);
        ctx.LineTo(new Point(x + w, y + h - br), true, true);
        if (br > 0.1)
            ctx.BezierTo(new Point(x + w, y + h - br * (1 - K)), new Point(x + w - br * (1 - K), y + h), new Point(x + w - br, y + h), true, true);
        ctx.LineTo(new Point(x + bl, y + h), true, true);
        if (bl > 0.1)
            ctx.BezierTo(new Point(x + bl * (1 - K), y + h), new Point(x, y + h - bl * (1 - K)), new Point(x, y + h - bl), true, true);
        ctx.LineTo(new Point(x, y + tl), true, true);
        if (tl > 0.1)
            ctx.BezierTo(new Point(x, y + tl * (1 - K)), new Point(x + tl * (1 - K), y), new Point(x + tl, y), true, true);
        sg.Freeze();
        return sg;
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
    private static double SmoothStep(double x)
    {
        x = Math.Clamp(x, 0, 1);
        return x * x * (3 - 2 * x);
    }
}
