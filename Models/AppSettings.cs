using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IslandWidget.Models;

public sealed class AppSettings
{
    // Dual-Monitor & Edge Docking
    // "Secondary" (default: right secondary monitor if present, else primary), "Primary", or specific Screen.DeviceName
    public string MonitorTarget { get; set; } = "Secondary";
    // "Top", "Bottom", "Left", "Right"
    public string DockEdge { get; set; } = "Top";
    // Normalized position along that edge (0.08 .. 0.92), 0.5 = centered
    public double EdgeRatio { get; set; } = 0.5;

    // Theme & Visuals ("dark", "light", "rouge", "sky")
    public string Theme { get; set; } = "dark";
    public double GlassOpacity { get; set; } = 0.90;
    public bool LiquidEffect { get; set; } = true;

    // Collapsed Mode ("overview", "clock", "media", "pet")
    public string CollapsedMode { get; set; } = "overview";
    public bool AutoShowLyrics { get; set; } = true;

    // Window & Power Behavior
    public bool ExpandOnHover { get; set; } = true;
    public bool PinnedCollapsed { get; set; } = false; // 双击切换“长关”
    public bool AlwaysOnTop { get; set; } = true;
    public bool ClickThrough { get; set; } = false;
    public bool HideOnFullscreen { get; set; } = true;
    public bool ShrinkOnMaximized { get; set; } = false;
    public bool PauseWhenLocked { get; set; } = true;
    public bool LowPowerMode { get; set; } = true;
    public bool StartWithWindows { get; set; } = false;

    // AI Quotas & Proxy
    public int QuotaRefreshMinutes { get; set; } = 5;
    public bool AutoDetectProxy { get; set; } = true;
    public string ManualProxyUrl { get; set; } = "";
    public int SystemProxyPort { get; set; } = 7890;
    public string FlClashPath { get; set; } = "";

    // Weather
    public string WeatherCity { get; set; } = "北京";
    public double? WeatherLat { get; set; } = 39.9042;
    public double? WeatherLon { get; set; } = 116.4074;

    // Remember last active tab (0..4)
    public int ActiveTabIndex { get; set; } = 0;

    [JsonIgnore]
    public static string SettingsDirectory =>
        Environment.GetEnvironmentVariable("ISLANDWIDGET_SETTINGS_DIR") is { Length: > 0 } dir
            ? Path.GetFullPath(dir)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IslandWidget");

    [JsonIgnore]
    public static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                var fresh = new AppSettings();
                fresh.AutoFillDefaults();
                return fresh;
            }
            var json = File.ReadAllText(SettingsPath);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            loaded.Normalize();
            return loaded;
        }
        catch
        {
            var fallback = new AppSettings();
            fallback.AutoFillDefaults();
            return fallback;
        }
    }

    public void Save()
    {
        try
        {
            Normalize();
            Directory.CreateDirectory(SettingsDirectory);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Keep working in memory if disk is read-only
        }
    }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    public void AutoFillDefaults()
    {
        if (string.IsNullOrWhiteSpace(FlClashPath))
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (Directory.Exists(desktop))
            {
                foreach (var dir in Directory.GetDirectories(desktop, "FlClash*"))
                {
                    var exe = Path.Combine(dir, "FlClash.exe");
                    if (File.Exists(exe))
                    {
                        FlClashPath = exe;
                        break;
                    }
                }
            }
        }
        Normalize();
    }

    public void Normalize()
    {
        GlassOpacity = Math.Clamp(GlassOpacity, 0.50, 1.0);
        EdgeRatio = Math.Clamp(EdgeRatio, 0.08, 0.92);
        QuotaRefreshMinutes = Math.Clamp(QuotaRefreshMinutes, 1, 60);
        SystemProxyPort = Math.Clamp(SystemProxyPort, 1, 65535);
        ManualProxyUrl = (ManualProxyUrl ?? "").Trim();
        WeatherCity = string.IsNullOrWhiteSpace(WeatherCity) ? "北京" : WeatherCity.Trim();
        if (DockEdge is not ("Top" or "Bottom" or "Left" or "Right"))
            DockEdge = "Top";
        if (Theme is not ("dark" or "light" or "rouge" or "sky"))
            Theme = "dark";
        if (CollapsedMode is not ("overview" or "clock" or "media" or "pet"))
            CollapsedMode = "overview";
        ActiveTabIndex = Math.Clamp(ActiveTabIndex, 0, 4);
    }

    [JsonIgnore]
    public string? EffectiveProxy => AutoDetectProxy
        ? ProxyDetector.DetectSystemProxy()
        : (string.IsNullOrWhiteSpace(ManualProxyUrl) ? null : ProxyDetector.Normalize(ManualProxyUrl));
}
