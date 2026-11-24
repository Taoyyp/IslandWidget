using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace IslandWidget.UI;

/// <summary>
/// BrimDeck-inspired crisp circular quota / battery ring with center glyph or letter.
/// </summary>
public sealed class QuotaRingControl : FrameworkElement
{
    private double _fraction = 0.8;
    private string _label = "A";
    private Color _baseColor = Color.FromRgb(167, 139, 250);
    private Brush _trackBrush = new SolidColorBrush(Color.FromArgb(48, 255, 255, 255));
    private Brush _textBrush = Brushes.White;
    private bool _ok = true;
    private bool _isFluentGlyph;

    public QuotaRingControl()
    {
        Width = 22;
        Height = 22;
    }

    public void SetRing(double remainingFraction, string label, string hexColor, ThemePalette theme, bool ok = true, bool isFluentGlyph = false)
    {
        _fraction = Math.Clamp(remainingFraction, 0, 1);
        _label = label;
        _ok = ok;
        _isFluentGlyph = isFluentGlyph;
        _trackBrush = theme.TrackBrush;
        _textBrush = theme.TextPrimary;

        try
        {
            if (ColorConverter.ConvertFromString(hexColor) is Color parsed)
                _baseColor = parsed;
        }
        catch { }

        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double w = ActualWidth > 0 ? ActualWidth : Width;
        double h = ActualHeight > 0 ? ActualHeight : Height;
        double size = Math.Min(w, h);
        if (size < 8) return;

        double stroke = Math.Max(2.2, size * 0.125);
        double radius = (size - stroke) / 2.0 - 0.5;
        var center = new Point(w / 2.0, h / 2.0);

        var trackPen = new Pen(_trackBrush, stroke);
        dc.DrawEllipse(null, trackPen, center, radius, radius);

        Color ringColor = !_ok
            ? Color.FromRgb(148, 163, 184)
            : _fraction <= 0.12
                ? Color.FromRgb(248, 113, 113) // Alert Red
                : _fraction <= 0.25
                    ? Color.FromRgb(251, 191, 36) // Warning Amber
                    : _baseColor;

        if (_ok && _fraction > 0.01)
        {
            var fgBrush = new SolidColorBrush(ringColor);
            fgBrush.Freeze();
            var arcPen = new Pen(fgBrush, stroke) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };

            if (_fraction >= 0.995)
            {
                dc.DrawEllipse(null, arcPen, center, radius, radius);
            }
            else
            {
                double sweepDeg = _fraction * 360.0;
                double startRad = -Math.PI / 2.0;
                double endRad = startRad + sweepDeg * Math.PI / 180.0;
                var startPt = new Point(center.X + radius * Math.Cos(startRad), center.Y + radius * Math.Sin(startRad));
                var endPt = new Point(center.X + radius * Math.Cos(endRad), center.Y + radius * Math.Sin(endRad));

                var sg = new StreamGeometry();
                using (var ctx = sg.Open())
                {
                    ctx.BeginFigure(startPt, false, false);
                    ctx.ArcTo(endPt, new System.Windows.Size(radius, radius), 0, sweepDeg > 180.0, SweepDirection.Clockwise, true, false);
                }
                sg.Freeze();
                dc.DrawGeometry(null, arcPen, sg);
            }
        }

        if (!string.IsNullOrEmpty(_label))
        {
            var tf = _isFluentGlyph
                ? new Typeface(new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal)
                : new Typeface(new System.Windows.Media.FontFamily("Segoe UI Variable Text, Microsoft YaHei UI, Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

            double fontSize = Math.Max(7.5, size * (_isFluentGlyph ? 0.44 : 0.42));
            var ft = new FormattedText(
                _label,
                CultureInfo.InvariantCulture,
                System.Windows.FlowDirection.LeftToRight,
                tf,
                fontSize,
                _textBrush,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);

            dc.DrawText(ft, new Point(center.X - ft.Width / 2.0, center.Y - ft.Height / 2.0));
        }
    }
}
