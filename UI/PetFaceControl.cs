using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace IslandWidget.UI;

/// <summary>
/// Ported from Wisland's PetFace (MIT): glowing cursor-tracking eyes with cute expressions & squish on press!
/// </summary>
public sealed class PetFaceControl : FrameworkElement
{
    private static readonly string[] Moods = ["happy", "wink", "love", "star", "shy", "squint"];
    private static readonly Random Rng = new();

    private double _lookX, _lookY;
    private double _curX, _curY;
    private double _squish;
    private string _mood = "default";
    private string _lastMood = "";
    private bool _near;
    private DateTime _nextBlink = DateTime.UtcNow.AddSeconds(2.5);
    private ThemePalette _theme = ThemeManager.Get("dark");

    public PetFaceControl()
    {
        Cursor = System.Windows.Input.Cursors.Hand;
        MouseDown += (_, _) =>
        {
            _squish = 1.0;
            PickMood();
            InvalidateVisual();
        };
    }

    public void SetTheme(ThemePalette theme)
    {
        _theme = theme;
        InvalidateVisual();
    }

    public void StepFrame(Point screenCursorDip, Point controlCenterScreenDip, bool isHovered)
    {
        double dx = screenCursorDip.X - controlCenterScreenDip.X;
        double dy = screenCursorDip.Y - controlCenterScreenDip.Y;
        bool near = double.Hypot(dx, dy) < 160 || isHovered;

        if (near && !_near) PickMood();
        else if (!near && _near) _mood = "default";
        _near = near;

        _lookX = Math.Clamp(dx / 90.0, -1.0, 1.0);
        _lookY = Math.Clamp(dy / 65.0, -1.0, 1.0);
        _curX += (_lookX - _curX) * 0.25;
        _curY += (_lookY - _curY) * 0.25;
        _squish *= 0.80;
        InvalidateVisual();
    }

    private void PickMood()
    {
        var choices = Moods.Where(m => m != _lastMood).ToArray();
        _mood = choices[Rng.Next(choices.Length)];
        _lastMood = _mood;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double w = ActualWidth > 0 ? ActualWidth : Width;
        double h = ActualHeight > 0 ? ActualHeight : Height;
        if (w < 10 || h < 10) return;

        double s = Math.Min(w / 44.0, h / 28.0);
        double cx = w / 2.0;
        double cy = h / 2.0;

        var now = DateTime.UtcNow;
        double blink = 0;
        if (now >= _nextBlink)
        {
            double elapsed = (now - _nextBlink).TotalSeconds;
            if (elapsed < 0.16)
                blink = Math.Sin(elapsed / 0.16 * Math.PI);
            else
                _nextBlink = now.AddSeconds(2.6 + Rng.NextDouble() * 3.0);
        }

        Brush eyeBrush = _theme.TextPrimary;
        Color glowColor = _mood == "love" ? Color.FromRgb(255, 92, 138) : _theme.AccentColor;

        double gap = (12.0 + _squish * 4.0) * s;
        double baseW = 8.5 * s * (1 + 0.18 * _squish);
        double baseH = 11.5 * s * (1 - 0.35 * _squish) * (1 - 0.85 * blink);

        for (int side = -1; side <= 1; side += 2)
        {
            double ex = cx + side * (gap / 2.0 + baseW / 2.0) + _curX * 4.2 * s;
            double ey = cy + _curY * 2.5 * s;

            // Soft glow halo in dark themes
            if (_theme.IsDark)
            {
                var rg = new RadialGradientBrush(Color.FromArgb(95, glowColor.R, glowColor.G, glowColor.B), Color.FromArgb(0, glowColor.R, glowColor.G, glowColor.B));
                rg.Freeze();
                dc.DrawEllipse(rg, null, new Point(ex, ey), baseW * 1.05, Math.Max(3, baseH * 0.95));
            }

            if (_mood == "love")
            {
                var heartBrush = new SolidColorBrush(Color.FromRgb(255, 92, 138));
                heartBrush.Freeze();
                dc.DrawGeometry(heartBrush, null, BuildHeart(ex, ey, 11.5 * s));
                continue;
            }

            if (_mood == "squint" || (_mood == "wink" && side > 0))
            {
                var pen = new Pen(eyeBrush, Math.Max(1.8, 2.2 * s)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                double d = -side;
                double k = 4.2 * s;
                dc.DrawLine(pen, new Point(ex - d * k, ey - k * 0.8), new Point(ex + d * k * 0.7, ey));
                dc.DrawLine(pen, new Point(ex + d * k * 0.7, ey), new Point(ex - d * k, ey + k * 0.8));
                continue;
            }

            var rect = new Rect(ex - baseW / 2.0, ey - Math.Max(1.5, baseH) / 2.0, baseW, Math.Max(1.5, baseH));
            double rad = Math.Min(rect.Width, rect.Height) * 0.44;
            Geometry eyeGeo = new RectangleGeometry(rect, rad, rad);

            if (_mood is "happy" or "shy")
            {
                var cut = new EllipseGeometry(new Point(ex, rect.Bottom + rect.Width * 0.55), rect.Width * 0.85, rect.Width * 0.85);
                eyeGeo = Geometry.Combine(eyeGeo, cut, GeometryCombineMode.Exclude, null);
            }

            dc.DrawGeometry(eyeBrush, null, eyeGeo);

            if (_mood == "star" && baseH > 5 * s)
            {
                var starFill = new SolidColorBrush(_theme.IsDark ? Color.FromRgb(24, 24, 34) : Colors.White);
                starFill.Freeze();
                dc.DrawEllipse(starFill, null, new Point(ex + 1.2 * s, ey - 1.8 * s), 2.0 * s, 2.0 * s);
            }

            if (_mood is "shy" or "love" or "happy")
            {
                var blush = new SolidColorBrush(Color.FromArgb(130, 255, 124, 162));
                blush.Freeze();
                dc.DrawEllipse(blush, null, new Point(ex + side * 5.8 * s, ey + 6.5 * s), 3.4 * s, 1.8 * s);
            }
        }
    }

    private static StreamGeometry BuildHeart(double cx, double cy, double sz)
    {
        var sg = new StreamGeometry();
        using var ctx = sg.Open();
        ctx.BeginFigure(new Point(cx, cy + sz * 0.42), true, true);
        ctx.BezierTo(new Point(cx - sz * 0.9, cy - sz * 0.05), new Point(cx - sz * 0.45, cy - sz * 0.75), new Point(cx, cy - sz * 0.26), true, true);
        ctx.BezierTo(new Point(cx + sz * 0.45, cy - sz * 0.75), new Point(cx + sz * 0.9, cy - sz * 0.05), new Point(cx, cy + sz * 0.42), true, true);
        sg.Freeze();
        return sg;
    }
}
