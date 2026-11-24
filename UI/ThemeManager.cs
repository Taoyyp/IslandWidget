using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;

namespace IslandWidget.UI;

public sealed class ThemePalette
{
    public required string Key { get; init; }
    public required string DisplayName { get; init; }
    public bool IsDark { get; init; }
    public required Brush IslandFill { get; init; }
    public required Brush RimBrush { get; init; }
    public required Brush CardBackground { get; init; }
    public required Brush CardBorder { get; init; }
    public required Brush HoverBrush { get; init; }
    public required Brush PillActiveBackground { get; init; }
    public required Brush PillActiveForeground { get; init; }
    public required Brush TextPrimary { get; init; }
    public required Brush TextSecondary { get; init; }
    public required Brush TextMuted { get; init; }
    public required Brush TrackBrush { get; init; }
    public required Brush AccentBrush { get; init; }
    public required Color AccentColor { get; init; }
    public required Color TextColor { get; init; }
}

public static class ThemeManager
{
    public static ThemePalette Get(string mode, double opacity = 0.92)
    {
        byte alpha = (byte)Math.Clamp((int)Math.Round(opacity * 255), 140, 252);
        return mode switch
        {
            "light" => BuildLight(alpha),
            "rouge" => BuildRouge(alpha),
            "sky" => BuildSky(alpha),
            _ => BuildDark(alpha)
        };
    }

    private static ThemePalette BuildDark(byte a) => new()
    {
        Key = "dark",
        DisplayName = "深色毛玻璃",
        IsDark = true,
        IslandFill = Frozen(Color.FromArgb(a, 18, 21, 30)),
        RimBrush = FrozenGradient(Color.FromArgb(88, 255, 255, 255), Color.FromArgb(24, 255, 255, 255)),
        CardBackground = Frozen(Color.FromArgb(26, 255, 255, 255)),
        CardBorder = Frozen(Color.FromArgb(32, 255, 255, 255)),
        HoverBrush = Frozen(Color.FromArgb(38, 255, 255, 255)),
        PillActiveBackground = Frozen(Color.FromArgb(65, 59, 130, 246)),
        PillActiveForeground = Frozen(Colors.White),
        TextPrimary = Frozen(Color.FromRgb(248, 250, 252)),
        TextSecondary = Frozen(Color.FromArgb(186, 241, 245, 249)),
        TextMuted = Frozen(Color.FromArgb(128, 226, 232, 240)),
        TrackBrush = Frozen(Color.FromArgb(42, 255, 255, 255)),
        AccentBrush = Frozen(Color.FromRgb(59, 130, 246)),
        AccentColor = Color.FromRgb(59, 130, 246),
        TextColor = Color.FromRgb(248, 250, 252)
    };

    private static ThemePalette BuildLight(byte a) => new()
    {
        Key = "light",
        DisplayName = "浅色毛玻璃",
        IsDark = false,
        IslandFill = Frozen(Color.FromArgb(Math.Max(a, (byte)225), 240, 243, 249)),
        RimBrush = FrozenGradient(Color.FromArgb(220, 255, 255, 255), Color.FromArgb(65, 15, 23, 42)),
        CardBackground = Frozen(Color.FromArgb(175, 255, 255, 255)),
        CardBorder = Frozen(Color.FromArgb(28, 15, 23, 42)),
        HoverBrush = Frozen(Color.FromArgb(20, 15, 23, 42)),
        PillActiveBackground = Frozen(Color.FromRgb(37, 99, 235)),
        PillActiveForeground = Frozen(Colors.White),
        TextPrimary = Frozen(Color.FromRgb(16, 20, 28)),
        TextSecondary = Frozen(Color.FromArgb(192, 30, 41, 59)),
        TextMuted = Frozen(Color.FromArgb(140, 51, 65, 85)),
        TrackBrush = Frozen(Color.FromArgb(34, 15, 23, 42)),
        AccentBrush = Frozen(Color.FromRgb(37, 99, 235)),
        AccentColor = Color.FromRgb(37, 99, 235),
        TextColor = Color.FromRgb(16, 20, 28)
    };

    private static ThemePalette BuildRouge(byte a) => new()
    {
        Key = "rouge",
        DisplayName = "胭脂撞色",
        IsDark = true,
        IslandFill = Frozen(Color.FromArgb(Math.Max(a, (byte)228), 8, 8, 12)),
        RimBrush = FrozenGradient(Color.FromArgb(185, 255, 124, 162), Color.FromArgb(48, 255, 124, 162)),
        CardBackground = Frozen(Color.FromArgb(26, 255, 255, 255)),
        CardBorder = Frozen(Color.FromArgb(52, 255, 124, 162)),
        HoverBrush = Frozen(Color.FromArgb(38, 255, 124, 162)),
        PillActiveBackground = Frozen(Color.FromRgb(255, 124, 162)),
        PillActiveForeground = Frozen(Color.FromRgb(12, 12, 16)),
        TextPrimary = Frozen(Colors.White),
        TextSecondary = Frozen(Color.FromArgb(190, 255, 240, 246)),
        TextMuted = Frozen(Color.FromArgb(132, 255, 228, 236)),
        TrackBrush = Frozen(Color.FromArgb(42, 255, 255, 255)),
        AccentBrush = Frozen(Color.FromRgb(255, 124, 162)),
        AccentColor = Color.FromRgb(255, 124, 162),
        TextColor = Colors.White
    };

    private static ThemePalette BuildSky(byte a) => new()
    {
        Key = "sky",
        DisplayName = "晴空渐变",
        IsDark = false,
        IslandFill = FrozenGradient(Color.FromArgb(Math.Max(a, (byte)225), 50, 171, 253), Color.FromArgb(Math.Max(a, (byte)235), 247, 239, 225)),
        RimBrush = FrozenGradient(Color.FromArgb(215, 255, 255, 255), Color.FromArgb(80, 12, 32, 56)),
        CardBackground = Frozen(Color.FromArgb(135, 255, 255, 255)),
        CardBorder = Frozen(Color.FromArgb(36, 12, 32, 56)),
        HoverBrush = Frozen(Color.FromArgb(165, 255, 255, 255)),
        PillActiveBackground = Frozen(Color.FromRgb(22, 103, 201)),
        PillActiveForeground = Frozen(Colors.White),
        TextPrimary = Frozen(Color.FromRgb(12, 32, 56)),
        TextSecondary = Frozen(Color.FromArgb(200, 12, 32, 56)),
        TextMuted = Frozen(Color.FromArgb(148, 12, 32, 56)),
        TrackBrush = Frozen(Color.FromArgb(38, 12, 32, 56)),
        AccentBrush = Frozen(Color.FromRgb(22, 103, 201)),
        AccentColor = Color.FromRgb(22, 103, 201),
        TextColor = Color.FromRgb(12, 32, 56)
    };

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static LinearGradientBrush FrozenGradient(Color top, Color bottom)
    {
        var g = new LinearGradientBrush(top, bottom, new System.Windows.Point(0, 0), new System.Windows.Point(0.2, 1));
        g.Freeze();
        return g;
    }
}
