using System.Windows;
using System.Windows.Controls;
using IslandWidget.Models;
using IslandWidget.Services;
using IslandWidget.UI;

namespace IslandWidget;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _working;
    private readonly WeatherService _weather;

    public AppSettings ResultSettings => _working;
    public bool ResetEdgeRatioRequested { get; private set; }

    public SettingsWindow(AppSettings current, WeatherService weather)
    {
        InitializeComponent();
        _working = current.Clone();
        _weather = weather;

        PopulateMonitors();
        SelectByTag(EdgeCombo, _working.DockEdge);
        SelectByTag(ThemeCombo, _working.Theme);
        SelectByTag(RefreshCombo, _working.QuotaRefreshMinutes.ToString());

        OpacitySlider.Value = Math.Round(_working.GlassOpacity * 100);
        OpacityText.Text = $"{(int)OpacitySlider.Value}%";

        LiquidCheck.IsChecked = _working.LiquidEffect;
        HoverExpandCheck.IsChecked = _working.ExpandOnHover;
        AutoLyricsCheck.IsChecked = _working.AutoShowLyrics;
        FullscreenHideCheck.IsChecked = _working.HideOnFullscreen;
        LowPowerCheck.IsChecked = _working.LowPowerMode;
        PauseLockCheck.IsChecked = _working.PauseWhenLocked;
        StartupCheck.IsChecked = StartupManager.IsEnabled();

        AutoProxyCheck.IsChecked = _working.AutoDetectProxy;
        ManualProxyBox.Text = _working.ManualProxyUrl;
        ManualProxyBox.IsEnabled = !_working.AutoDetectProxy;
        ProxyPortBox.Text = _working.SystemProxyPort.ToString();
        CityBox.Text = _working.WeatherCity;
        if (_working.WeatherLat.HasValue && _working.WeatherLon.HasValue)
            GeoHintText.Text = $"当前坐标：{_working.WeatherLat:0.##}, {_working.WeatherLon:0.##}";
    }

    private void PopulateMonitors()
    {
        MonitorCombo.Items.Clear();
        MonitorCombo.Items.Add(new ComboBoxItem { Content = "自动选择副屏（推荐，双屏右屏不挡工作区）", Tag = "Secondary" });
        MonitorCombo.Items.Add(new ComboBoxItem { Content = "自动选择主屏（左主屏）", Tag = "Primary" });

        var screens = DualMonitorHelper.GetAllScreens();
        for (int i = 0; i < screens.Count; i++)
        {
            var s = screens[i];
            MonitorCombo.Items.Add(new ComboBoxItem
            {
                Content = DualMonitorHelper.DescribeScreen(s, i),
                Tag = s.DeviceName
            });
        }
        SelectByTag(MonitorCombo, _working.MonitorTarget);
    }

    private static void SelectByTag(System.Windows.Controls.ComboBox combo, string tag)
    {
        foreach (var obj in combo.Items)
        {
            if (obj is ComboBoxItem item && string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OpacityText is not null)
            OpacityText.Text = $"{(int)Math.Round(e.NewValue)}%";
    }

    private void AutoProxyCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (ManualProxyBox is not null)
            ManualProxyBox.IsEnabled = AutoProxyCheck.IsChecked != true;
    }

    private async void GeoBtn_Click(object sender, RoutedEventArgs e)
    {
        var q = CityBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(q)) return;
        GeoBtn.IsEnabled = false;
        GeoHintText.Text = "正在查询城市坐标…";
        try
        {
            var res = await _weather.GeocodeCityAsync(q, _working);
            if (res.Found)
            {
                _working.WeatherCity = res.ResolvedName;
                _working.WeatherLat = res.Lat;
                _working.WeatherLon = res.Lon;
                CityBox.Text = res.ResolvedName;
                GeoHintText.Text = $"已定位：{res.ResolvedName} ({res.Lat:0.##}, {res.Lon:0.##})";
            }
            else
            {
                GeoHintText.Text = "未找到该城市，请尝试输入中文标准市名或拼音";
            }
        }
        finally
        {
            GeoBtn.IsEnabled = true;
        }
    }

    private void ResetCenter_Click(object sender, RoutedEventArgs e)
    {
        _working.EdgeRatio = 0.5;
        ResetEdgeRatioRequested = true;
        GeoHintText.Text = "已重置为所选屏幕边缘正中央 (50%)";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (MonitorCombo.SelectedItem is ComboBoxItem mItem)
            _working.MonitorTarget = mItem.Tag?.ToString() ?? "Secondary";
        if (EdgeCombo.SelectedItem is ComboBoxItem eItem)
            _working.DockEdge = eItem.Tag?.ToString() ?? "Top";
        if (ThemeCombo.SelectedItem is ComboBoxItem tItem)
            _working.Theme = tItem.Tag?.ToString() ?? "dark";
        if (RefreshCombo.SelectedItem is ComboBoxItem rItem && int.TryParse(rItem.Tag?.ToString(), out var mins))
            _working.QuotaRefreshMinutes = mins;

        _working.GlassOpacity = OpacitySlider.Value / 100.0;
        _working.LiquidEffect = LiquidCheck.IsChecked == true;
        _working.ExpandOnHover = HoverExpandCheck.IsChecked == true;
        _working.AutoShowLyrics = AutoLyricsCheck.IsChecked == true;
        _working.HideOnFullscreen = FullscreenHideCheck.IsChecked == true;
        _working.LowPowerMode = LowPowerCheck.IsChecked == true;
        _working.PauseWhenLocked = PauseLockCheck.IsChecked == true;
        _working.StartWithWindows = StartupCheck.IsChecked == true;
        StartupManager.SetEnabled(_working.StartWithWindows);

        _working.AutoDetectProxy = AutoProxyCheck.IsChecked == true;
        _working.ManualProxyUrl = ManualProxyBox.Text.Trim();
        if (int.TryParse(ProxyPortBox.Text.Trim(), out var port) && port is > 0 and <= 65535)
            _working.SystemProxyPort = port;

        var newCity = CityBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(newCity) && !string.Equals(newCity, _working.WeatherCity, StringComparison.OrdinalIgnoreCase))
        {
            _working.WeatherCity = newCity;
            _working.WeatherLat = null;
            _working.WeatherLon = null;
        }

        _working.Normalize();
        DialogResult = true;
        Close();
    }
}
