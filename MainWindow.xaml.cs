using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using IslandWidget.Models;
using IslandWidget.Services;
using IslandWidget.UI;
using Microsoft.Win32;
using Application = System.Windows.Application;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using Forms = System.Windows.Forms;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Orientation = System.Windows.Controls.Orientation;
using Point = System.Windows.Point;

namespace IslandWidget;

public partial class MainWindow : Window
{
    private const double CollapsedHeight = 36;
    private const double ExpandedWidth = 536;
    private const double ExpandedHeight = 346;
    private const double DragMarginPad = 76; // Extra transparent margin while stretching liquid neck

    private static readonly string[] CollapsedModes = ["overview", "clock", "media", "pet"];
    private static readonly string[] ThemeOrder = ["dark", "light", "rouge", "sky"];

    private readonly AppSettings _settings;
    private readonly bool _demoMode;

    // Core Services
    private readonly NativeQuotaService _quotaService = new();
    private readonly HardwareMonitorService _hwService = new();
    private readonly WirelessBatteryService _batteryService = new();
    private readonly MediaLyricsService _mediaService = new();
    private readonly ClipboardShelfService _clipboardService = new();
    private readonly WeatherService _weatherService = new();
    private readonly SystemControlService _sysControl = new();

    // Timers (all paused when locked/sleeping; ZERO high-FPS timer when idle!)
    private readonly DispatcherTimer _fastTick;     // 2.5s collapsed / 1.0s expanded: CPU/RAM/GPU/Net + Media + Fullscreen check
    private readonly DispatcherTimer _batteryTick;  // 45s: Wireless & Bluetooth battery
    private readonly DispatcherTimer _quotaTick;    // N min: AI Quotas
    private readonly DispatcherTimer _weatherTick;  // 30 min: Open-Meteo weather
    private readonly DispatcherTimer _hoverExpandTimer;
    private readonly DispatcherTimer _hoverLeaveTimer;

    // Animation & Liquid Drag State
    private const double TransitionDurationSec = 0.18;
    private bool _renderHooked;
    private bool _isExpanded;
    private bool _pinnedExpanded;
    private bool _contextMenuOpen;
    private bool _windowInCanvasMode;
    private double _animLinear; // 0 = collapsed, 1 = expanded (linear time progress)
    private double _targetLinear;
    private int _outsideHoverTicks;
    private Rect _lastBodyScreenDip;
    private long _lastFrameTicks;

    private bool _pointerDown;
    private bool _draggingIsland;
    private bool _liquidDetached;
    private System.Drawing.Point _dragStartCursorPhys;
    private double _dragStartWindowLeft, _dragStartWindowTop;
    private double _pullGap;
    private double _dripRadius;
    private DateTime _lastLeftClickUtc = DateTime.MinValue;
    private Point _shelfDragStart;

    // Runtime States
    private ThemePalette _palette;
    private QuotaAllResult _lastQuotas = new();
    private HardwareSnapshot _lastHw;
    private MediaTrackSnapshot _lastMedia = MediaTrackSnapshot.Empty;
    private IntPtr _hwnd;
    private Forms.NotifyIcon? _trayIcon;
    private bool _sessionPaused;
    private bool _hiddenByFullscreen;
    private bool _updatingSliders;
    private bool _quotaBusy;
    private int _quotaFailStreak;
    private bool _shuttingDown;

    public MainWindow(AppSettings settings, bool demoMode)
    {
        _settings = settings;
        _demoMode = demoMode;
        _palette = ThemeManager.Get(_settings.Theme, _settings.GlassOpacity);

        InitializeComponent();

        ClipboardListBox.ItemsSource = _clipboardService.History;
        ShelfListBox.ItemsSource = _clipboardService.ShelfFiles;
        _clipboardService.ShelfFiles.CollectionChanged += (_, _) =>
        {
            ShelfEmptyHint.Visibility = _clipboardService.ShelfFiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        };

        _fastTick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2.2) };
        _fastTick.Tick += FastTick_Tick;

        _batteryTick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(45) };
        _batteryTick.Tick += async (_, _) => await RefreshBatteriesAsync();

        _quotaTick = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMinutes(Math.Clamp(_settings.QuotaRefreshMinutes, 1, 60))
        };
        _quotaTick.Tick += async (_, _) => await RefreshQuotasAsync();

        _weatherTick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(30) };
        _weatherTick.Tick += async (_, _) => await RefreshWeatherAsync();

        _hoverExpandTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
        _hoverExpandTimer.Tick += (_, _) =>
        {
            _hoverExpandTimer.Stop();
            if (_isExpanded || !_settings.ExpandOnHover || _settings.PinnedCollapsed || _draggingIsland || _settings.ClickThrough || _contextMenuOpen)
                return;

            // Keep small-window direct-click quick buttons usable without jumping open!
            if (IsCursorOverDirectActionButtons())
                return;

            var cursorDip = DualMonitorHelper.PhysicalToDipPoint(this, Forms.Cursor.Position);
            var hitRect = _lastBodyScreenDip;
            hitRect.Inflate(4, 4);
            if (hitRect.Contains(cursorDip))
                ExpandIsland();
        };

        _hoverLeaveTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(95) };
        _hoverLeaveTimer.Tick += (_, _) =>
        {
            if (!_isExpanded || _pinnedExpanded || _draggingIsland || _contextMenuOpen)
            {
                _outsideHoverTicks = 0;
                if (!_isExpanded) _hoverLeaveTimer.Stop();
                return;
            }

            var cursorDip = DualMonitorHelper.PhysicalToDipPoint(this, Forms.Cursor.Position);
            var activeRect = _lastBodyScreenDip;
            activeRect.Inflate(14, 14);
            if (activeRect.Contains(cursorDip))
            {
                _outsideHoverTicks = 0;
            }
            else
            {
                _outsideHoverTicks++;
                if (_outsideHoverTicks >= 2) // ~190ms outside actual glass bounds
                {
                    _outsideHoverTicks = 0;
                    _hoverLeaveTimer.Stop();
                    CollapseIsland();
                }
            }
        };

        SourceInitialized += MainWindow_SourceInitialized;
        Loaded += MainWindow_Loaded;
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;

        // ToolWindow + NoActivate when collapsed so hovering/clicking media never steals focus from user's active IDE!
        const int GWL_EXSTYLE = -20;
        const int WS_EX_TOOLWINDOW = 0x00000080;
        int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW);

        var src = HwndSource.FromHwnd(_hwnd);
        src?.AddHook(WndProc);

        _clipboardService.AttachWindow(this);
        DualMonitorHelper.SetClickThrough(this, _settings.ClickThrough);
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        SetupTrayIcon();
        ApplyThemePalette();
        SwitchTab(_settings.ActiveTabIndex);
        UpdateCollapsedModeVisibility();
        RepositionAndRedraw();

        try
        {
            SystemEvents.SessionSwitch += SystemEvents_SessionSwitch;
            SystemEvents.PowerModeChanged += SystemEvents_PowerModeChanged;
            SystemEvents.DisplaySettingsChanged += SystemEvents_DisplaySettingsChanged;
        }
        catch { }

        // Initial immediate sampling
        SampleHardwareAndClock();
        SyncControlSliders();

        _fastTick.Start();
        _batteryTick.Start();
        _quotaTick.Start();
        _weatherTick.Start();

        await Task.WhenAll(
            RefreshBatteriesAsync(),
            RefreshQuotasAsync(),
            RefreshWeatherAsync(),
            PollMediaSafeAsync());

        // Trim initial JIT/WinRT metadata pages once startup settles so idle RAM stays minimal
        var startupTrim = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(4) };
        startupTrim.Tick += (_, _) => { startupTrim.Stop(); if (!_isExpanded) App.TrimMemory(); };
        startupTrim.Start();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_DISPLAYCHANGE = 0x007E;
        const int WM_DPICHANGED = 0x02E0;
        if (msg is WM_DISPLAYCHANGE or WM_DPICHANGED)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () => RepositionAndRedraw());
        }
        return IntPtr.Zero;
    }

    // ==================== LIFECYCLE & LOW-POWER SLEEP ====================

    private void SystemEvents_DisplaySettingsChanged(object? sender, EventArgs e)
        => Dispatcher.BeginInvoke(DispatcherPriority.Background, () => RepositionAndRedraw());

    private void SystemEvents_SessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (!_settings.PauseWhenLocked) return;
        if (e.Reason == SessionSwitchReason.SessionLock) SetPaused(true);
        else if (e.Reason == SessionSwitchReason.SessionUnlock) SetPaused(false);
    }

    private void SystemEvents_PowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) SetPaused(true);
        else if (e.Mode == PowerModes.Resume) SetPaused(false);
    }

    private void SetPaused(bool pause)
    {
        _sessionPaused = pause;
        if (pause)
        {
            _fastTick.Stop();
            _batteryTick.Stop();
            _quotaTick.Stop();
            _weatherTick.Stop();
            EnsureRenderLoop(false);
        }
        else
        {
            _fastTick.Start();
            _batteryTick.Start();
            _quotaTick.Start();
            _weatherTick.Start();
            SampleHardwareAndClock();
            _ = RefreshBatteriesAsync();
            _ = RefreshQuotasAsync();
        }
    }

    public void ShutdownCleanly()
    {
        if (_shuttingDown) return;
        _shuttingDown = true;

        _fastTick.Stop();
        _batteryTick.Stop();
        _quotaTick.Stop();
        _weatherTick.Stop();
        _hoverExpandTimer.Stop();
        _hoverLeaveTimer.Stop();
        EnsureRenderLoop(false);

        try
        {
            SystemEvents.SessionSwitch -= SystemEvents_SessionSwitch;
            SystemEvents.PowerModeChanged -= SystemEvents_PowerModeChanged;
            SystemEvents.DisplaySettingsChanged -= SystemEvents_DisplaySettingsChanged;
        }
        catch { }

        _clipboardService.Dispose();
        _batteryService.Dispose();
        _hwService.Dispose();
        _quotaService.Dispose();
        _mediaService.Dispose();
        _weatherService.Dispose();

        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
    }

    // ==================== EXPAND / COLLAPSE / ZERO-IDLE RENDER LOOP ====================

    public void ExpandIsland(bool pin = false)
    {
        if (_settings.ClickThrough) return;
        _hoverExpandTimer.Stop();
        _outsideHoverTicks = 0;
        if (pin)
        {
            _pinnedExpanded = true;
            UpdatePinVisual();
        }
        _isExpanded = true;
        _targetLinear = 1.0;
        _fastTick.Interval = TimeSpan.FromSeconds(1.0);
        SyncControlSliders();
        // Start expand animation directly inside the already-allocated transparent canvas -> ZERO window resize flicker!
        RepositionAndRedraw(forceWindowUpdate: false);
        if (!_pinnedExpanded) _hoverLeaveTimer.Start();
        EnsureRenderLoop(true);
    }

    public void CollapseIsland()
    {
        _hoverExpandTimer.Stop();
        _hoverLeaveTimer.Stop();
        _outsideHoverTicks = 0;
        _pinnedExpanded = false;
        UpdatePinVisual();
        _isExpanded = false;
        _targetLinear = 0.0;
        _fastTick.Interval = TimeSpan.FromSeconds(_settings.LowPowerMode ? 2.5 : 1.5);
        EnsureRenderLoop(true);

        // Gently trim working set 3s after collapsing so memory stays rock-bottom!
        var trimTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(3) };
        trimTimer.Tick += (_, _) =>
        {
            trimTimer.Stop();
            if (!_isExpanded) App.TrimMemory();
        };
        trimTimer.Start();
    }

    private void EnsureRenderLoop(bool needActive)
    {
        bool petVisible = !_sessionPaused && !_hiddenByFullscreen &&
                          ((_isExpanded && _settings.ActiveTabIndex == 2) ||
                           (!_isExpanded && _animLinear <= 0.001 && EffectiveCollapsedMode() == "pet"));

        bool inTransition = Math.Abs(_targetLinear - _animLinear) > 0.0005;
        bool shouldHook = !_sessionPaused && (needActive || inTransition || petVisible || _dripRadius > 0.4);

        if (shouldHook && !_renderHooked)
        {
            _lastFrameTicks = Stopwatch.GetTimestamp();
            CompositionTarget.Rendering += CompositionTarget_Rendering;
            _renderHooked = true;
        }
        else if (!shouldHook && _renderHooked)
        {
            CompositionTarget.Rendering -= CompositionTarget_Rendering;
            _renderHooked = false;
        }
    }

    private void CompositionTarget_Rendering(object? sender, EventArgs e)
    {
        long now = Stopwatch.GetTimestamp();
        double dt = Math.Clamp((double)(now - _lastFrameTicks) / Stopwatch.Frequency, 0.001, 0.05);
        _lastFrameTicks = now;

        bool animating = false;

        // Deterministic linear-time transition (180ms exact, no lingering slow tail!)
        if (Math.Abs(_targetLinear - _animLinear) > 0.0005)
        {
            double step = dt / TransitionDurationSec;
            if (_targetLinear > _animLinear)
                _animLinear = Math.Min(_targetLinear, _animLinear + step);
            else
                _animLinear = Math.Max(_targetLinear, _animLinear - step);

            animating = true;
            if (_animLinear <= 0.0005 && _targetLinear == 0.0)
            {
                _animLinear = 0.0;
            }
        }

        // Retracting liquid droplet after snapping off edge
        if (_dripRadius > 0.4)
        {
            _dripRadius *= Math.Pow(0.03, dt);
            if (_dripRadius <= 0.45)
                _dripRadius = 0;
            animating = true;
        }

        // Step pet eye-tracking only if visible
        var cursorDip = DualMonitorHelper.PhysicalToDipPoint(this, Forms.Cursor.Position);
        if (!_isExpanded && _animLinear <= 0.001 && EffectiveCollapsedMode() == "pet" && CollapsedPetFace.IsVisible)
        {
            var center = CollapsedPetFace.PointToScreen(new Point(CollapsedPetFace.ActualWidth / 2, CollapsedPetFace.ActualHeight / 2));
            var centerDip = DualMonitorHelper.PhysicalToDipPoint(this, new System.Drawing.Point((int)center.X, (int)center.Y));
            CollapsedPetFace.StepFrame(cursorDip, centerDip, IsMouseOver);
        }
        else if (_isExpanded && _animLinear >= 0.99 && _settings.ActiveTabIndex == 2 && ExpandedPetFace.IsVisible)
        {
            var center = ExpandedPetFace.PointToScreen(new Point(ExpandedPetFace.ActualWidth / 2, ExpandedPetFace.ActualHeight / 2));
            var centerDip = DualMonitorHelper.PhysicalToDipPoint(this, new System.Drawing.Point((int)center.X, (int)center.Y));
            ExpandedPetFace.StepFrame(cursorDip, centerDip, IsMouseOver);
        }

        if (animating)
        {
            // Never resize Win32 window during expand/collapse frames -> 100% flicker-free!
            RepositionAndRedraw(forceWindowUpdate: false);
        }
        else
        {
            // Unhook immediately if settled and pet isn't active -> 0 FPS idle!
            EnsureRenderLoop(false);
        }
    }

    // ==================== DUAL-MONITOR GEOMETRY & LIQUID DRAG ====================

    private double CollapsedCapsuleWidth()
    {
        switch (EffectiveCollapsedMode())
        {
            case "overview":
                // Auto-layout width calculation based on enabled & visible compact modules
                double w = 26; // Horizontal padding
                if (_settings.CompactShowQuotas) w += 82;
                if (_settings.CompactShowHardware) w += 172;
                if (_settings.CompactShowBattery && ColBatBadge.Visibility == Visibility.Visible) w += 64;
                if (_settings.CompactShowQuickActions) w += 82;
                if (_settings.CompactShowClock) w += 44;
                return Math.Clamp(w, 160, 520);
            case "clock":
                return 264;
            case "media":
                return 410;
            case "pet":
                return 190;
            default:
                return 380;
        }
    }

    private string EffectiveCollapsedMode()
    {
        if (_settings.AutoShowLyrics && _lastMedia.HasSession && _lastMedia.IsPlaying)
            return "media";
        return _settings.CollapsedMode;
    }

    private void RepositionAndRedraw() => RepositionAndRedraw(forceWindowUpdate: true);

    private void RepositionAndRedraw(bool forceWindowUpdate)
    {
        if (_draggingIsland && _liquidDetached)
        {
            double freeW = CollapsedCapsuleWidth();
            double freeH = CollapsedHeight;
            SetWindowBoundsAtomic(Left, Top, freeW + 16, freeH + 16);
            var freeRect = new Rect(8, 8, freeW, freeH);
            PlaceBody(freeRect);
            LiquidSurface.Width = Width;
            LiquidSurface.Height = Height;
            LiquidSurface.UpdateState(_palette, "Free", freeRect, freeH / 2.0);
            _lastBodyScreenDip = new Rect(Left + freeRect.Left, Top + freeRect.Top, freeRect.Width, freeRect.Height);
            return;
        }

        var screen = DualMonitorHelper.ResolveScreen(_settings.MonitorTarget);
        var boundsDip = DualMonitorHelper.PhysicalToDipRect(this, screen.Bounds);
        var workDip = DualMonitorHelper.PhysicalToDipRect(this, screen.WorkingArea);

        double colW = CollapsedCapsuleWidth();
        // Smooth asymmetric cubic easing: fast snap-open on expand, fast shrink-down on collapse
        double eased = _targetLinear > 0.5
            ? 1.0 - Math.Pow(1.0 - _animLinear, 3.0)
            : Math.Pow(_animLinear, 2.2);

        double curW = Lerp(colW, ExpandedWidth, eased);
        double curH = Lerp(CollapsedHeight, ExpandedHeight, eased);
        double radius = Lerp(CollapsedHeight / 2.0, 20.0, eased);

        // Asymmetric cross-fade: CollapsedLayer ONLY appears when the pill has already shrunk back to capsule size!
        if (_animLinear <= 0.0005)
        {
            CollapsedLayer.Visibility = Visibility.Visible;
            CollapsedLayer.Opacity = 1.0;
            ExpandedLayer.Visibility = Visibility.Collapsed;
            ExpandedLayer.Opacity = 0.0;
        }
        else if (_targetLinear > 0.5) // Expanding
        {
            if (_animLinear < 0.22)
            {
                CollapsedLayer.Visibility = Visibility.Visible;
                CollapsedLayer.Opacity = Math.Clamp(1.0 - _animLinear / 0.22, 0, 1);
                ExpandedLayer.Visibility = Visibility.Collapsed;
                ExpandedLayer.Opacity = 0.0;
            }
            else
            {
                CollapsedLayer.Visibility = Visibility.Collapsed;
                CollapsedLayer.Opacity = 0.0;
                ExpandedLayer.Visibility = Visibility.Visible;
                ExpandedLayer.Opacity = Math.Clamp((_animLinear - 0.22) / 0.58, 0, 1);
            }
        }
        else // Collapsing
        {
            if (_animLinear > 0.45)
            {
                ExpandedLayer.Visibility = Visibility.Visible;
                ExpandedLayer.Opacity = Math.Clamp((_animLinear - 0.45) / 0.55, 0, 1);
                CollapsedLayer.Visibility = Visibility.Collapsed;
                CollapsedLayer.Opacity = 0.0;
            }
            else if (_animLinear > 0.16)
            {
                // Clean glass pill shrinking quickly without showing collapsed content inside a large box
                ExpandedLayer.Visibility = Visibility.Collapsed;
                ExpandedLayer.Opacity = 0.0;
                CollapsedLayer.Visibility = Visibility.Collapsed;
                CollapsedLayer.Opacity = 0.0;
            }
            else
            {
                ExpandedLayer.Visibility = Visibility.Collapsed;
                ExpandedLayer.Opacity = 0.0;
                CollapsedLayer.Visibility = Visibility.Visible;
                CollapsedLayer.Opacity = Math.Clamp(1.0 - _animLinear / 0.16, 0, 1);
            }
        }

        string edge = _settings.DockEdge;
        double pull = _draggingIsland ? _pullGap : 0;
        Rect area = edge == "Bottom" ? workDip : boundsDip;

        // Keep the transparent layered window at a constant max canvas size for the docked edge.
        // In WPF AllowsTransparency=True, Alpha=0 pixels outside BodyContainer are natively click-through,
        // so expand/collapse NEVER resizes the Win32 window -> 100% zero flicker!
        double targetWinW, targetWinH, targetWinL, targetWinT;
        if (edge is "Top" or "Bottom")
        {
            targetWinW = Math.Max(ExpandedWidth, colW) + 20;
            targetWinH = Math.Max(ExpandedHeight + 16, CollapsedHeight + DragMarginPad);
            double cx = area.Left + area.Width * _settings.EdgeRatio;
            targetWinL = Math.Clamp(cx - targetWinW / 2.0, area.Left, area.Right - targetWinW);
            targetWinT = edge == "Top" ? area.Top : (area.Bottom - targetWinH);
        }
        else
        {
            targetWinW = Math.Max(ExpandedWidth + 16, colW + DragMarginPad);
            targetWinH = ExpandedHeight + 20;
            double cy = workDip.Top + workDip.Height * _settings.EdgeRatio;
            targetWinL = edge == "Left" ? area.Left : (area.Right - targetWinW);
            targetWinT = Math.Clamp(cy - targetWinH / 2.0, workDip.Top, workDip.Bottom - targetWinH);
        }

        if (forceWindowUpdate || !_windowInCanvasMode || _draggingIsland)
        {
            _windowInCanvasMode = true;
            if (Math.Abs(Left - targetWinL) > 0.5) Left = targetWinL;
            if (Math.Abs(Top - targetWinT) > 0.5) Top = targetWinT;
            if (Math.Abs(Width - targetWinW) > 0.5) Width = targetWinW;
            if (Math.Abs(Height - targetWinH) > 0.5) Height = targetWinH;
        }

        // Compute exact bodyRect inside the fixed window canvas
        double winW = Width;
        double winH = Height;
        Rect bodyRect;
        if (edge == "Top")
        {
            double cx = area.Left + area.Width * _settings.EdgeRatio;
            double localCx = Math.Clamp(cx - Left, curW / 2.0 + 10, winW - curW / 2.0 - 10);
            bodyRect = new Rect(localCx - curW / 2.0, pull, curW, curH);
        }
        else if (edge == "Bottom")
        {
            double cx = area.Left + area.Width * _settings.EdgeRatio;
            double localCx = Math.Clamp(cx - Left, curW / 2.0 + 10, winW - curW / 2.0 - 10);
            bodyRect = new Rect(localCx - curW / 2.0, winH - curH - pull, curW, curH);
        }
        else if (edge == "Left")
        {
            double cy = workDip.Top + workDip.Height * _settings.EdgeRatio;
            double localCy = Math.Clamp(cy - Top, curH / 2.0 + 10, winH - curH / 2.0 - 10);
            bodyRect = new Rect(pull, localCy - curH / 2.0, curW, curH);
        }
        else // Right
        {
            double cy = workDip.Top + workDip.Height * _settings.EdgeRatio;
            double localCy = Math.Clamp(cy - Top, curH / 2.0 + 10, winH - curH / 2.0 - 10);
            bodyRect = new Rect(winW - curW - pull, localCy - curH / 2.0, curW, curH);
        }

        PlaceBody(bodyRect);
        _lastBodyScreenDip = new Rect(Left + bodyRect.Left, Top + bodyRect.Top, bodyRect.Width, bodyRect.Height);

        double neckStrength = (_settings.LiquidEffect && pull > 0.5 && !_liquidDetached) ? 1.0 : 0.0;
        double anchor = edge is "Top" or "Bottom" ? (bodyRect.Left + bodyRect.Width / 2.0) : (bodyRect.Top + bodyRect.Height / 2.0);
        LiquidSurface.Width = winW;
        LiquidSurface.Height = winH;
        LiquidSurface.UpdateState(_palette, edge, bodyRect, radius, pull, neckStrength, _dripRadius, anchor);
    }

    private void SetWindowBoundsAtomic(double leftDip, double topDip, double widthDip, double heightDip)
    {
        if (Math.Abs(Left - leftDip) > 0.5) Left = leftDip;
        if (Math.Abs(Top - topDip) > 0.5) Top = topDip;
        if (Math.Abs(Width - widthDip) > 0.5) Width = widthDip;
        if (Math.Abs(Height - heightDip) > 0.5) Height = heightDip;
    }

    private void PlaceBody(Rect r)
    {
        BodyContainer.Margin = new Thickness(r.Left, r.Top, 0, 0);
        BodyContainer.Width = r.Width;
        BodyContainer.Height = r.Height;
    }

    private void BodyContainer_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject dep && IsInsideInteractiveControl(dep))
            return;

        // Manual double-click detection on collapsed capsule -> toggles "长关" (PinnedCollapsed)
        var now = DateTime.UtcNow;
        if (!_isExpanded && (now - _lastLeftClickUtc).TotalMilliseconds < 320)
        {
            _lastLeftClickUtc = DateTime.MinValue;
            _settings.PinnedCollapsed = !_settings.PinnedCollapsed;
            _settings.Save();
            FooterLeftText.Text = _settings.PinnedCollapsed ? "已开启长关模式（双击恢复悬停展开）" : "已恢复悬停自动展开";
            if (!_settings.PinnedCollapsed && _settings.ExpandOnHover)
                ExpandIsland();
            e.Handled = true;
            return;
        }
        _lastLeftClickUtc = now;

        _pointerDown = true;
        _draggingIsland = false;
        _liquidDetached = false;
        _pullGap = 0;
        _dragStartCursorPhys = Forms.Cursor.Position;
        _dragStartWindowLeft = Left;
        _dragStartWindowTop = Top;
        BodyContainer.CaptureMouse();
    }

    private void BodyContainer_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_pointerDown) return;

        var curPhys = Forms.Cursor.Position;
        int dxPhys = curPhys.X - _dragStartCursorPhys.X;
        int dyPhys = curPhys.Y - _dragStartCursorPhys.Y;

        if (!_draggingIsland && double.Hypot(dxPhys, dyPhys) < 6)
            return;

        if (!_draggingIsland)
        {
            _draggingIsland = true;
            _hoverExpandTimer.Stop();
            _hoverLeaveTimer.Stop();
            if (_isExpanded)
            {
                _isExpanded = false;
                _pinnedExpanded = false;
                _animLinear = 0;
                _targetLinear = 0;
            }
        }

        var startDip = DualMonitorHelper.PhysicalToDipPoint(this, _dragStartCursorPhys);
        var curDip = DualMonitorHelper.PhysicalToDipPoint(this, curPhys);
        double dx = curDip.X - startDip.X;
        double dy = curDip.Y - startDip.Y;

        if (!_liquidDetached)
        {
            // Calculate perpendicular pull distance from docked edge & slide along edge!
            var screen = DualMonitorHelper.ResolveScreen(_settings.MonitorTarget);
            var area = DualMonitorHelper.PhysicalToDipRect(this, _settings.DockEdge == "Bottom" ? screen.WorkingArea : screen.Bounds);

            double pull = _settings.DockEdge switch
            {
                "Top" => Math.Max(0, curDip.Y - area.Top - CollapsedHeight / 2.0),
                "Bottom" => Math.Max(0, area.Bottom - curDip.Y - CollapsedHeight / 2.0),
                "Left" => Math.Max(0, curDip.X - area.Left - CollapsedCapsuleWidth() / 2.0),
                _ => Math.Max(0, area.Right - curDip.X - CollapsedCapsuleWidth() / 2.0)
            };

            if (_settings.DockEdge is "Top" or "Bottom")
                _settings.EdgeRatio = Math.Clamp((curDip.X - area.Left) / Math.Max(1, area.Width), 0.08, 0.92);
            else
                _settings.EdgeRatio = Math.Clamp((curDip.Y - area.Top) / Math.Max(1, area.Height), 0.10, 0.90);

            if (!_settings.LiquidEffect || pull > 66.0)
            {
                // SNAP! Liquid bridge breaks -> free-floating capsule across screens!
                _liquidDetached = true;
                _pullGap = 0;
                _dripRadius = _settings.LiquidEffect ? 10.0 : 0;
            }
            else
            {
                // Elastic surface-tension resistance
                _pullGap = pull * 0.82;
                RepositionAndRedraw();
                return;
            }
        }

        if (_liquidDetached)
        {
            var curDipPt = DualMonitorHelper.PhysicalToDipPoint(this, curPhys);
            RepositionAndRedraw();
            Left = curDipPt.X - Width / 2.0;
            Top = curDipPt.Y - Height / 2.0;
        }
    }

    private void BodyContainer_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pointerDown) return;
        _pointerDown = false;
        BodyContainer.ReleaseMouseCapture();

        if (_draggingIsland)
        {
            _draggingIsland = false;
            if (_liquidDetached)
            {
                SnapToNearestMonitorAndEdge(Forms.Cursor.Position);
                _liquidDetached = false;
            }
            _pullGap = 0;
            _dripRadius = _settings.LiquidEffect ? 8.5 : 0;
            _settings.Save();
            UpdateDualScreenLabels();
            RepositionAndRedraw();
            EnsureRenderLoop(true);
            return;
        }

        // Simple click when collapsed (if hover-expand is off or pinned-collapsed wasn't triggered) -> expand!
        if (!_isExpanded && !_settings.PinnedCollapsed)
        {
            ExpandIsland();
        }
    }

    private void SnapToNearestMonitorAndEdge(System.Drawing.Point cursorPhys)
    {
        var screen = Forms.Screen.FromPoint(cursorPhys);
        var screens = DualMonitorHelper.GetAllScreens();

        // Save friendly logical target if 2-screen setup, or exact device name if 3+ screens
        if (screens.Count <= 2)
            _settings.MonitorTarget = screen.Primary ? "Primary" : "Secondary";
        else
            _settings.MonitorTarget = screen.DeviceName;

        var b = screen.WorkingArea;
        double dTop = Math.Abs(cursorPhys.Y - screen.Bounds.Top);
        double dBottom = Math.Abs(b.Bottom - cursorPhys.Y);
        double dLeft = Math.Abs(cursorPhys.X - b.Left);
        double dRight = Math.Abs(b.Right - cursorPhys.X);

        // Bias slightly toward Top & Bottom horizontal edges (most ergonomic for capsule text!)
        double min = Math.Min(Math.Min(dTop * 0.75, dBottom * 0.85), Math.Min(dLeft, dRight));
        if (min == dTop * 0.75)
        {
            _settings.DockEdge = "Top";
            _settings.EdgeRatio = Math.Clamp((double)(cursorPhys.X - screen.Bounds.Left) / Math.Max(1, screen.Bounds.Width), 0.08, 0.92);
        }
        else if (min == dBottom * 0.85)
        {
            _settings.DockEdge = "Bottom";
            _settings.EdgeRatio = Math.Clamp((double)(cursorPhys.X - b.Left) / Math.Max(1, b.Width), 0.08, 0.92);
        }
        else if (min == dLeft)
        {
            _settings.DockEdge = "Left";
            _settings.EdgeRatio = Math.Clamp((double)(cursorPhys.Y - b.Top) / Math.Max(1, b.Height), 0.12, 0.88);
        }
        else
        {
            _settings.DockEdge = "Right";
            _settings.EdgeRatio = Math.Clamp((double)(cursorPhys.Y - b.Top) / Math.Max(1, b.Height), 0.12, 0.88);
        }
    }

    private static bool IsInsideInteractiveControl(DependencyObject? dep)
    {
        while (dep is not null)
        {
            if (dep is System.Windows.Controls.Primitives.ButtonBase or System.Windows.Controls.Slider or System.Windows.Controls.ListBoxItem or System.Windows.Controls.TextBox or PetFaceControl)
                return true;
            dep = VisualTreeHelper.GetParent(dep);
        }
        return false;
    }

    // ==================== HOVER, WHEEL & FILE DROP ====================

    private void BodyContainer_MouseEnter(object sender, MouseEventArgs e)
    {
        _outsideHoverTicks = 0;
        if (!_isExpanded && _settings.ExpandOnHover && !_settings.PinnedCollapsed && !_draggingIsland && !_settings.ClickThrough && !_contextMenuOpen)
        {
            // Small intent delay (120ms) prevents accidental flash-open and lets user click small-window quick icons directly!
            _hoverExpandTimer.Stop();
            _hoverExpandTimer.Start();
        }
    }

    private bool IsCursorOverDirectActionButtons()
    {
        if (CompactQuickActionsBox.IsVisible && CompactQuickActionsBox.IsMouseOver)
            return true;
        if (CollapsedMediaPanel.IsVisible && ColPlayPauseBtn.IsMouseOver)
            return true;
        return false;
    }

    private void BodyContainer_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_isExpanded) return;
        CycleCollapsedMode(e.Delta > 0 ? -1 : 1);
        e.Handled = true;
    }

    private void CycleCollapsedMode(int delta)
    {
        int idx = Array.IndexOf(CollapsedModes, _settings.CollapsedMode);
        if (idx < 0) idx = 0;
        idx = (idx + delta + CollapsedModes.Length) % CollapsedModes.Length;
        _settings.CollapsedMode = CollapsedModes[idx];
        // Temporarily override AutoShowLyrics if user manually scrolled away from media
        if (_settings.CollapsedMode != "media" && _lastMedia.IsPlaying)
            _settings.AutoShowLyrics = false;
        _settings.Save();
        UpdateCollapsedModeVisibility();
        RepositionAndRedraw();
        EnsureRenderLoop(false);
    }

    private void RootCanvas_DragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            if (!_isExpanded) ExpandIsland();
            SwitchTab(3); // Auto-switch to Clipboard & File Shelf tab!
            e.Handled = true;
        }
    }

    private void RootCanvas_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void RootCanvas_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            int count = _clipboardService.AddDroppedFiles(files);
            FooterLeftText.Text = $"已暂存 {count} 个文件到中转站";
            SwitchTab(3);
            e.Handled = true;
        }
    }

    private void ShelfListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _shelfDragStart = e.GetPosition(null);
    }

    private void ShelfListBox_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _shelfDragStart.X) < 6 && Math.Abs(pos.Y - _shelfDragStart.Y) < 6)
            return;

        if (ShelfListBox.SelectedItem is ShelfFileEntry entry && (File.Exists(entry.FullPath) || Directory.Exists(entry.FullPath)))
        {
            var data = new System.Windows.DataObject(DataFormats.FileDrop, new[] { entry.FullPath });
            DragDrop.DoDragDrop(ShelfListBox, data, DragDropEffects.Copy | DragDropEffects.Move);
        }
    }

    // ==================== PERIODIC SAMPLING & DATA BINDING ====================

    private async void FastTick_Tick(object? sender, EventArgs e)
    {
        if (_sessionPaused || _shuttingDown) return;

        // 1. Fullscreen Auto-Hide check on target monitor
        if (_settings.HideOnFullscreen && !_isExpanded && !_draggingIsland)
        {
            var screen = DualMonitorHelper.ResolveScreen(_settings.MonitorTarget);
            var (isFull, _) = DualMonitorHelper.CheckForegroundOnScreen(screen, _hwnd);
            if (isFull != _hiddenByFullscreen)
            {
                _hiddenByFullscreen = isFull;
                Opacity = isFull ? 0.0 : 1.0;
                IsHitTestVisible = !isFull;
            }
        }
        else if (_hiddenByFullscreen)
        {
            _hiddenByFullscreen = false;
            Opacity = 1.0;
            IsHitTestVisible = true;
        }

        if (_hiddenByFullscreen) return;

        // 2. Sample CPU / RAM / GPU / Network + Clock
        SampleHardwareAndClock();

        // 2b. If any 2.4G mouse is marked Asleep and user just moved mouse/keyboard (<1.5s ago), wake-refresh battery!
        if (_batteryService.CurrentDevices.Any(d => d.Asleep && !d.BatteryPercent.HasValue) && GetUserIdleMs() < 1500)
        {
            _ = RefreshBatteriesAsync();
        }

        // 3. Poll Media & Lyrics
        await PollMediaSafeAsync();
    }

    private void SampleHardwareAndClock()
    {
        bool wantGpuTemp = _isExpanded && _settings.ActiveTabIndex == 1;
        _lastHw = _hwService.Sample(wantGpuTemp);

        var now = DateTime.Now;
        string timeStr = now.ToString("HH:mm");
        string[] dows = ["周日", "周一", "周二", "周三", "周四", "周五", "周六"];

        // Collapsed Overview
        ColHwText.Text = $"CPU {_lastHw.CpuPercent}% · GPU {_lastHw.GpuPercent}%";
        ColNetText.Text = $"↓{HardwareSnapshot.FormatRate(_lastHw.NetDownloadBps)}";
        ColTimeText.Text = timeStr;

        // Collapsed Clock & Pet
        ClockBigTime.Text = timeStr;
        ClockDateText.Text = $"{now:MM/dd} {dows[(int)now.DayOfWeek]}";
        var w = _weatherService.Current;
        string wSummary = w.Ok ? $"{w.Glyph} {w.TempC}°C {w.Desc}" : $"{w.Glyph} --°C";
        ClockWeatherGlyph.Text = w.Glyph;
        ClockWeatherText.Text = w.Ok ? $"{w.TempC}°C {w.Desc}" : w.Desc;
        PetTimeWeatherText.Text = $"{timeStr} · {(w.Ok ? $"{w.Glyph} {w.TempC}°C" : "")}".TrimEnd(' ', '·');

        // Expanded Hardware Page (update DOM only when expanded!)
        if (_isExpanded)
        {
            HwCpuBig.Text = $"{_lastHw.CpuPercent}%";
            HwCpuBar.Value = _lastHw.CpuPercent;

            HwRamSub.Text = $"内存 {_lastHw.RamUsedGb:0.1}/{_lastHw.RamTotalGb:0.#}G";
            HwRamBig.Text = $"{_lastHw.RamPercent}%";
            HwRamBar.Value = _lastHw.RamPercent;

            HwGpuSub.Text = _lastHw.GpuVramTotalGb > 0
                ? $"{_lastHw.GpuName} ({_lastHw.GpuVramUsedGb:0.1}/{_lastHw.GpuVramTotalGb:0.#}G)"
                : _lastHw.GpuName;
            HwGpuBig.Text = $"{_lastHw.GpuPercent}%";
            HwGpuTemp.Text = _lastHw.GpuTempC.HasValue ? $"{_lastHw.GpuTempC.Value}°C" : "";
            HwGpuBar.Value = _lastHw.GpuPercent;

            HwNetDown.Text = $"↓ {HardwareSnapshot.FormatRate(_lastHw.NetDownloadBps)}";
            HwNetUp.Text = $"↑ {HardwareSnapshot.FormatRate(_lastHw.NetUploadBps)}";
        }
    }

    private async Task PollMediaSafeAsync()
    {
        var prevMode = EffectiveCollapsedMode();
        _lastMedia = await _mediaService.PollAsync(_settings.IncludeBrowserVideoMedia);
        var nextMode = EffectiveCollapsedMode();

        if (prevMode != nextMode)
        {
            UpdateCollapsedModeVisibility();
            RepositionAndRedraw();
        }

        // Collapsed Media Bar
        ColCoverImg.Source = _lastMedia.Cover;
        ColPlayPauseBtn.Content = _lastMedia.IsPlaying ? "\uE769" : "\uE768";
        if (_lastMedia.HasSession)
        {
            string line = !string.IsNullOrWhiteSpace(_lastMedia.CurrentLyric)
                ? (_lastMedia.CurrentTransLyric.Length > 0 ? $"{_lastMedia.CurrentLyric} · {_lastMedia.CurrentTransLyric}" : _lastMedia.CurrentLyric)
                : $"{_lastMedia.Title}{(string.IsNullOrWhiteSpace(_lastMedia.Artist) ? "" : " - " + _lastMedia.Artist)}";
            ColLyricText.Text = line;
        }
        else
        {
            ColLyricText.Text = "暂无正在播放的音乐";
        }

        // Expanded Media Page
        if (_isExpanded)
        {
            ExpCoverImg.Source = _lastMedia.Cover;
            ExpPlayPauseBtn.Content = _lastMedia.IsPlaying ? "\uE769" : "\uE768";
            if (_lastMedia.HasSession)
            {
                ExpSongTitle.Text = _lastMedia.Title;
                ExpSongArtist.Text = string.IsNullOrWhiteSpace(_lastMedia.Artist) ? _lastMedia.Album : _lastMedia.Artist;
                ExpMediaAppBadge.Text = $"来源：{_lastMedia.AppName}";
                ExpLyricMain.Text = !string.IsNullOrWhiteSpace(_lastMedia.CurrentLyric) ? _lastMedia.CurrentLyric : $"♪ {_lastMedia.Title}";
                ExpLyricTrans.Text = _lastMedia.CurrentTransLyric;
                ExpMediaProgress.Value = _lastMedia.DurationSec > 0
                    ? Math.Clamp(_lastMedia.PositionSec * 100.0 / _lastMedia.DurationSec, 0, 100)
                    : 0;
            }
            else
            {
                ExpSongTitle.Text = "暂无正在播放的音乐";
                ExpSongArtist.Text = _settings.IncludeBrowserVideoMedia
                    ? "支持音乐软件 / 浏览器视频 (SMTC)"
                    : "默认仅识别音乐软件（网易云 / QQ / 酷狗 / Spotify 等）";
                ExpMediaAppBadge.Text = "";
                ExpLyricMain.Text = "♪ 播放音乐时自动同步滚动双语歌词";
                ExpLyricTrans.Text = "";
                ExpMediaProgress.Value = 0;
            }
        }
    }

    private async Task RefreshQuotasAsync()
    {
        if (_quotaBusy || _shuttingDown) return;
        _quotaBusy = true;
        FooterRightText.Text = "正在刷新额度…";
        try
        {
            _lastQuotas = await _quotaService.FetchAllAsync(_settings, _demoMode);
            if (_lastQuotas.AnyOk)
            {
                _quotaFailStreak = 0;
                _quotaTick.Interval = TimeSpan.FromMinutes(Math.Clamp(_settings.QuotaRefreshMinutes, 1, 60));
            }
            else
            {
                _quotaFailStreak = Math.Min(3, _quotaFailStreak + 1);
                int backoff = Math.Min(60, _settings.QuotaRefreshMinutes * (1 << _quotaFailStreak));
                _quotaTick.Interval = TimeSpan.FromMinutes(backoff);
            }
            BindQuotaUi();
            FooterRightText.Text = $"更新于 {_lastQuotas.UpdatedAt:HH:mm}";
        }
        finally
        {
            _quotaBusy = false;
        }
    }

    private void BindQuotaUi()
    {
        var agy = _lastQuotas.Antigravity;
        var cdx = _lastQuotas.Codex;
        var dsh = _lastQuotas.Dsh;

        RingAgy.SetRing(agy.RingRemainingFraction, "A", agy.AccentHex, _palette, agy.Ok);
        RingCodex.SetRing(cdx.RingRemainingFraction, "C", cdx.AccentHex, _palette, cdx.Ok);
        RingDsh.SetRing(dsh.RingRemainingFraction, "¥", dsh.AccentHex, _palette, dsh.Ok);

        RingAgy.ToolTip = $"Antigravity: {(agy.Ok ? agy.CompactSummary : agy.Error)}";
        RingCodex.ToolTip = $"Codex: {(cdx.Ok ? cdx.CompactSummary : cdx.Error)}";
        RingDsh.ToolTip = $"DSH: {(dsh.Ok ? dsh.CompactSummary : dsh.Error)}";

        // Antigravity Card
        AgySubText.Text = agy.Subtitle;
        if (agy.Ok)
        {
            AgyRow1Val.Text = agy.Primary is not null ? $"{agy.Primary.RemainingPercent}%" : "—";
            AgyRow1Reset.Text = agy.Primary?.ResetText ?? "";
            AgyBar1.Value = agy.Primary?.RemainingPercent ?? 0;

            AgyRow2Val.Text = agy.Secondary is not null ? $"{agy.Secondary.RemainingPercent}%" : "—";
            AgyRow2Reset.Text = agy.Secondary?.ResetText ?? "";
            AgyBar2.Value = agy.Secondary?.RemainingPercent ?? 0;
            AgyStatusNote.Text = agy.Note;
        }
        else
        {
            AgyRow1Val.Text = "未就绪";
            AgyRow1Reset.Text = "";
            AgyBar1.Value = 0;
            AgyRow2Val.Text = "—";
            AgyRow2Reset.Text = "";
            AgyBar2.Value = 0;
            AgyStatusNote.Text = agy.Error;
        }

        // Codex Card
        CodexSubText.Text = cdx.Subtitle;
        if (cdx.Ok)
        {
            CodexRow1Val.Text = cdx.Primary is not null ? $"{cdx.Primary.RemainingPercent}%" : "—";
            CodexRow1Reset.Text = cdx.Primary?.ResetText ?? "";
            CodexBar1.Value = cdx.Primary?.RemainingPercent ?? 0;

            CodexRow2Val.Text = cdx.Secondary is not null ? $"{cdx.Secondary.RemainingPercent}%" : "—";
            CodexRow2Reset.Text = cdx.Secondary?.ResetText ?? "";
            CodexBar2.Value = cdx.Secondary?.RemainingPercent ?? 0;
            CodexStatusNote.Text = "";
        }
        else
        {
            CodexRow1Val.Text = "未就绪";
            CodexRow1Reset.Text = "";
            CodexBar1.Value = 0;
            CodexRow2Val.Text = "—";
            CodexRow2Reset.Text = "";
            CodexBar2.Value = 0;
            CodexStatusNote.Text = cdx.Error;
        }

        // DSH Card
        if (dsh.Ok)
        {
            DshBalanceVal.Text = $"¥{dsh.Balance:0.00}";
            DshBonusVal.Text = $"¥{dsh.Bonus:0.00}";
            DshSpentVal.Text = $"¥{dsh.Spent:0.00}";
            DshStatusNote.Text = "原生直连 · 零 Node 子进程";
        }
        else
        {
            DshBalanceVal.Text = "—";
            DshBonusVal.Text = "—";
            DshSpentVal.Text = "—";
            DshStatusNote.Text = dsh.Error;
        }
    }

    private async Task RefreshBatteriesAsync()
    {
        if (_shuttingDown) return;
        var devices = await _batteryService.RefreshAsync(_demoMode);
        BindWirelessDevicesUi(devices);
    }

    private void BindWirelessDevicesUi(IReadOnlyList<WirelessDeviceStatus> devices)
    {
        // 1. Collapsed Battery Pill: pick primary device with battery%, or first connected receiver
        var allSummary = string.Join("\n", devices.Select(d => $"{d.Name} ({d.ConnectionType}): {d.DisplayBattery}"));
        var primaryWithPct = devices.FirstOrDefault(d => d.BatteryPercent.HasValue);
        if (_settings.CompactShowBattery && primaryWithPct is not null)
        {
            ColBatBadge.Visibility = Visibility.Visible;
            ColBatIcon.Text = primaryWithPct.IconGlyph;
            ColBatText.Text = primaryWithPct.Charging ? $"⚡{primaryWithPct.BatteryPercent}%" : $"{primaryWithPct.BatteryPercent}%";
            ColBatBadge.ToolTip = allSummary + "\n（点击展开并刷新电量）";
        }
        else if (_settings.CompactShowBattery && devices.Count > 0)
        {
            ColBatBadge.Visibility = Visibility.Visible;
            ColBatIcon.Text = devices[0].IconGlyph;
            ColBatText.Text = devices[0].Asleep ? "休眠" : "2.4G";
            ColBatBadge.ToolTip = allSummary + "\n（点击展开并刷新电量）";
        }
        else
        {
            ColBatBadge.Visibility = Visibility.Collapsed;
        }
        RepositionAndRedraw(forceWindowUpdate: false);

        // 2. Expanded Wireless Devices List
        WirelessDevicesList.Children.Clear();
        if (devices.Count == 0)
        {
            WirelessDevicesList.Children.Add(new TextBlock
            {
                Text = "暂未检测到已连接的蓝牙或 2.4G 无线外设",
                FontSize = 11.5,
                Opacity = 0.65,
                Margin = new Thickness(4, 12, 4, 0)
            });
            return;
        }

        foreach (var d in devices)
        {
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var ring = new QuotaRingControl { Width = 24, Height = 24, Margin = new Thickness(0, 0, 9, 0) };
            double frac = d.BatteryPercent.HasValue ? d.BatteryPercent.Value / 100.0 : 1.0;
            ring.SetRing(frac, d.IconGlyph, "#34D399", _palette, ok: d.BatteryPercent.HasValue, isFluentGlyph: true);
            Grid.SetColumn(ring, 0);
            row.Children.Add(ring);

            var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            mid.Children.Add(new TextBlock { Text = d.Name, FontSize = 12, FontWeight = FontWeights.SemiBold });
            mid.Children.Add(new TextBlock { Text = $"{d.ConnectionType} 连接", FontSize = 10, Opacity = 0.62 });
            Grid.SetColumn(mid, 1);
            row.Children.Add(mid);

            var rightColor = !d.BatteryPercent.HasValue
                ? _palette.TextSecondary
                : d.BatteryPercent.Value <= 15
                    ? new SolidColorBrush(Color.FromRgb(248, 113, 113))
                    : d.BatteryPercent.Value <= 25
                        ? new SolidColorBrush(Color.FromRgb(251, 191, 36))
                        : _palette.TextPrimary;

            var batTxt = new TextBlock
            {
                Text = d.DisplayBattery,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = rightColor,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(batTxt, 2);
            row.Children.Add(batTxt);

            WirelessDevicesList.Children.Add(row);
        }
    }

    private async Task RefreshWeatherAsync()
    {
        if (_shuttingDown) return;
        var w = await _weatherService.RefreshAsync(_settings, _demoMode);
        ExpWeatherCity.Text = w.City;
        ExpWeatherHum.Text = w.Ok ? $"湿度 {w.Humidity}% · 风 {w.WindSpeedMs:0.#}m/s" : "离线";
        ExpWeatherGlyph.Text = w.Glyph;
        ExpWeatherTemp.Text = w.Ok ? $"{w.TempC}°C" : "--°C";
        ExpWeatherDesc.Text = w.Ok ? $"{w.Desc} (体感 {w.FeelsLikeC}°)" : w.Desc;
        WeatherForecastList.ItemsSource = w.Forecast;
        SampleHardwareAndClock();
    }

    // ==================== THEMES, TABS & CONTROLS ====================

    private void ApplyThemePalette()
    {
        _palette = ThemeManager.Get(_settings.Theme, _settings.GlassOpacity);
        Foreground = _palette.TextPrimary;

        Border[] cards =
        [
            CardAgy, CardCodex, CardDsh,
            HwCardCpu, HwCardRam, HwCardGpu, HwCardNet, HwCardDevices,
            MediaCard, WeatherCard, PetCard,
            ClipCard, ShelfCard, CtrlSlidersCard
        ];
        foreach (var c in cards)
        {
            c.Background = _palette.CardBackground;
            c.BorderBrush = _palette.CardBorder;
        }

        Button[] headerBtns = [PinBtn, SwitchScreenBtn, ThemeQuickBtn, OpenSettingsBtn, CollapseNowBtn, ColMuteBtn, ColScreenBtn];
        foreach (var b in headerBtns)
        {
            b.Background = _palette.CardBackground;
            b.Foreground = _palette.TextPrimary;
        }
        RefreshProxyTile();

        Button[] tiles = [TileProxyBtn, TileScreenBtn, TileBtBtn, TileCollapsedModeBtn, TileFullscreenBtn, TileRefreshAllBtn];
        foreach (var t in tiles)
        {
            t.Background = _palette.CardBackground;
            t.BorderBrush = _palette.CardBorder;
            t.Foreground = _palette.TextPrimary;
        }

        CollapsedPetFace.SetTheme(_palette);
        ExpandedPetFace.SetTheme(_palette);
        UpdatePinVisual();
        UpdateTabPillStyles();
        UpdateDualScreenLabels();
        BindQuotaUi();
        BindWirelessDevicesUi(_batteryService.CurrentDevices);
        RepositionAndRedraw();
    }

    private void SwitchTab(int index)
    {
        index = Math.Clamp(index, 0, 4);
        _settings.ActiveTabIndex = index;

        PageQuotas.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        PageHardware.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        PageMediaWeather.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        PageClipboardShelf.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
        PageControls.Visibility = index == 4 ? Visibility.Visible : Visibility.Collapsed;

        UpdateTabPillStyles();
        if (index == 4) SyncControlSliders();
        EnsureRenderLoop(_animLinear > 0 && _animLinear < 1);
    }

    private void UpdateTabPillStyles()
    {
        Button[] tabs = [TabBtn0, TabBtn1, TabBtn2, TabBtn3, TabBtn4];
        for (int i = 0; i < tabs.Length; i++)
        {
            bool active = i == _settings.ActiveTabIndex;
            tabs[i].Background = active ? _palette.PillActiveBackground : Brushes.Transparent;
            tabs[i].Foreground = active ? _palette.PillActiveForeground : _palette.TextSecondary;
        }
    }

    private void UpdateCollapsedModeVisibility()
    {
        string mode = EffectiveCollapsedMode();
        CollapsedOverviewPanel.Visibility = mode == "overview" ? Visibility.Visible : Visibility.Collapsed;
        CollapsedClockPanel.Visibility = mode == "clock" ? Visibility.Visible : Visibility.Collapsed;
        CollapsedMediaPanel.Visibility = mode == "media" ? Visibility.Visible : Visibility.Collapsed;
        CollapsedPetPanel.Visibility = mode == "pet" ? Visibility.Visible : Visibility.Collapsed;

        // Auto-layout module visibility inside Overview capsule
        CompactQuotasBox.Visibility = _settings.CompactShowQuotas ? Visibility.Visible : Visibility.Collapsed;
        CompactHwBox.Visibility = _settings.CompactShowHardware ? Visibility.Visible : Visibility.Collapsed;
        CompactQuickActionsBox.Visibility = _settings.CompactShowQuickActions ? Visibility.Visible : Visibility.Collapsed;
        ColTimeText.Visibility = _settings.CompactShowClock ? Visibility.Visible : Visibility.Collapsed;
        ColDivider1.Visibility = (_settings.CompactShowQuotas && (_settings.CompactShowHardware || _settings.CompactShowBattery))
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (!_settings.CompactShowBattery)
            ColBatBadge.Visibility = Visibility.Collapsed;

        TileCollapsedModeSub.Text = _settings.CollapsedMode switch
        {
            "overview" => "综合灵动 (自动排版+快捷点击)",
            "clock" => "时间与实时天气",
            "media" => "音乐与滚动歌词",
            "pet" => "Wisland 发光双眼小形象",
            _ => "综合灵动"
        };
    }

    private void UpdatePinVisual()
    {
        PinBtn.Background = _pinnedExpanded ? _palette.PillActiveBackground : _palette.CardBackground;
        PinBtn.Foreground = _pinnedExpanded ? _palette.PillActiveForeground : _palette.TextPrimary;
    }

    private void UpdateDualScreenLabels()
    {
        var screen = DualMonitorHelper.ResolveScreen(_settings.MonitorTarget);
        string scrName = screen.Primary ? "左主屏" : "右副屏";
        if (DualMonitorHelper.GetAllScreens().Count == 1) scrName = "主屏幕";
        string edgeName = _settings.DockEdge switch
        {
            "Top" => "顶部",
            "Bottom" => "底部",
            "Left" => "左缘",
            _ => "右缘"
        };
        TileScreenSub.Text = $"{scrName} · {edgeName}";
        TileFullscreenSub.Text = _settings.HideOnFullscreen ? "已开启 (全屏自动避让)" : "已关闭";
    }

    private async void SyncControlSliders()
    {
        _updatingSliders = true;
        try
        {
            var (vol, muted) = _sysControl.GetMasterAudio();
            VolumeSlider.Value = vol;
            VolumeValText.Text = muted ? "静音" : $"{vol}%";
            MuteBtn.Content = muted ? "\uE74F" : "\uE767";
            ColMuteBtn.Content = muted ? "\uE74F" : "\uE767";

            var br = _sysControl.GetBrightness(_hwnd);
            if (br.HasValue)
            {
                BrightnessSlider.IsEnabled = true;
                BrightnessSlider.Value = br.Value;
                BrightnessValText.Text = $"{br.Value}%";
            }
            else
            {
                BrightnessValText.Text = "外置";
            }

            RefreshProxyTile();
            var (btOn, _) = await _sysControl.GetRadioStatesAsync();
            TileBtSub.Text = btOn ? "已开启 · 右键打开设置" : "未开启 / 点击切换";
        }
        finally
        {
            _updatingSliders = false;
        }
    }

    private void RefreshProxyTile()
    {
        var (clashRun, clientName, proxyOn, srv) = _sysControl.GetProxyStatus();
        TileProxyTitle.Text = clashRun ? $"🌐 {clientName} (运行中)" : "🌐 Clash 系代理开关";
        TileProxySub.Text = proxyOn ? $"已开启 ({(string.IsNullOrWhiteSpace(srv) ? $"127.0.0.1:{_settings.SystemProxyPort}" : srv)})" : "直连 (点击开启系统代理)";
        TileProxyBtn.Background = proxyOn ? _palette.PillActiveBackground : _palette.CardBackground;
        TileProxyBtn.Foreground = proxyOn ? _palette.PillActiveForeground : _palette.TextPrimary;

        ColProxyBtn.Background = proxyOn ? _palette.PillActiveBackground : _palette.CardBackground;
        ColProxyBtn.Foreground = proxyOn ? _palette.PillActiveForeground : _palette.TextPrimary;
        ColProxyBtn.ToolTip = proxyOn
            ? $"{clientName} 系统代理已开启（左键关闭，右键打开客户端）"
            : $"{clientName} 当前直连（左键开启系统代理，右键打开客户端）";
    }

    // ==================== BUTTON & INTERACTION HANDLERS ====================

    private void TabBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && int.TryParse(btn.Tag?.ToString(), out var idx))
        {
            SwitchTab(idx);
            _settings.Save();
        }
    }

    private void PinBtn_Click(object sender, RoutedEventArgs e)
    {
        _pinnedExpanded = !_pinnedExpanded;
        UpdatePinVisual();
    }

    private void SwitchScreenBtn_Click(object sender, RoutedEventArgs e)
    {
        var screens = DualMonitorHelper.GetAllScreens();
        if (screens.Count < 2)
        {
            FooterLeftText.Text = "当前仅检测到一块显示器";
            return;
        }

        var current = DualMonitorHelper.ResolveScreen(_settings.MonitorTarget);
        _settings.MonitorTarget = current.Primary ? "Secondary" : "Primary";
        _settings.EdgeRatio = 0.5;
        _settings.Save();
        UpdateDualScreenLabels();
        RepositionAndRedraw();
        FooterLeftText.Text = $"已切换至 {(current.Primary ? "右副屏" : "左主屏")} 边缘中央";
    }

    private void ThemeQuickBtn_Click(object sender, RoutedEventArgs e)
    {
        int idx = Array.IndexOf(ThemeOrder, _settings.Theme);
        _settings.Theme = ThemeOrder[(idx + 1) % ThemeOrder.Length];
        _settings.Save();
        ApplyThemePalette();
        FooterLeftText.Text = $"已切换主题：{_palette.DisplayName}";
    }

    private void OpenSettingsBtn_Click(object sender, RoutedEventArgs e) => ShowSettingsDialog();

    private void CollapseNowBtn_Click(object sender, RoutedEventArgs e) => CollapseIsland();

    private async void RefreshBattery_Click(object sender, RoutedEventArgs e)
    {
        FooterLeftText.Text = "正在重新扫描 2.4G / 蓝牙无线外设电量…";
        await RefreshBatteriesAsync();
        FooterLeftText.Text = $"无线外设扫描完成（共 {_batteryService.CurrentDevices.Count} 台）";
    }

    private async void MediaPlayPause_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        await _mediaService.PlayPauseAsync();
        await Task.Delay(180);
        await PollMediaSafeAsync();
    }

    private async void MediaPrev_Click(object sender, RoutedEventArgs e)
    {
        await _mediaService.PreviousAsync();
        await Task.Delay(200);
        await PollMediaSafeAsync();
    }

    private async void MediaNext_Click(object sender, RoutedEventArgs e)
    {
        await _mediaService.NextAsync();
        await Task.Delay(200);
        await PollMediaSafeAsync();
    }

    private void ClipboardListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ClipboardListBox.SelectedItem is ClipboardEntry item)
        {
            _clipboardService.CopyEntryToClipboard(item);
            FooterLeftText.Text = $"已复制到剪贴板：{item.Preview}";
            ClipboardListBox.SelectedItem = null;
        }
    }

    private void ClearClipboard_Click(object sender, RoutedEventArgs e)
    {
        _clipboardService.History.Clear();
        FooterLeftText.Text = "剪贴板历史已清空";
    }

    private void ClearShelf_Click(object sender, RoutedEventArgs e)
    {
        _clipboardService.ShelfFiles.Clear();
        FooterLeftText.Text = "文件中转站已清空";
    }

    private void CopyAllShelf_Click(object sender, RoutedEventArgs e)
    {
        _clipboardService.CopyShelfFilesToClipboard();
        FooterLeftText.Text = $"已将 {_clipboardService.ShelfFiles.Count} 个暂存文件复制到剪贴板，可直接 Ctrl+V 粘贴";
    }

    private void CompactQuotasBox_Click(object sender, MouseButtonEventArgs e)
    {
        if (_draggingIsland) return;
        SwitchTab(0);
        ExpandIsland(pin: false);
        e.Handled = true;
    }

    private void CompactHwBox_Click(object sender, MouseButtonEventArgs e)
    {
        if (_draggingIsland) return;
        SwitchTab(1);
        ExpandIsland(pin: false);
        e.Handled = true;
    }

    private async void ColBatBadge_Click(object sender, MouseButtonEventArgs e)
    {
        if (_draggingIsland) return;
        SwitchTab(1);
        ExpandIsland(pin: false);
        e.Handled = true;
        await RefreshBatteriesAsync();
    }

    private void MuteBtn_Click(object sender, RoutedEventArgs e)
    {
        bool muted = _sysControl.ToggleMute();
        MuteBtn.Content = muted ? "\uE74F" : "\uE767";
        ColMuteBtn.Content = muted ? "\uE74F" : "\uE767";
        VolumeValText.Text = muted ? "静音" : $"{(int)VolumeSlider.Value}%";
        e.Handled = true;
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingSliders || VolumeValText is null) return;
        int v = (int)Math.Round(e.NewValue);
        _sysControl.SetMasterVolume(v);
        VolumeValText.Text = $"{v}%";
        MuteBtn.Content = v == 0 ? "\uE74F" : "\uE767";
    }

    private void BrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingSliders || BrightnessValText is null) return;
        int b = (int)Math.Round(e.NewValue);
        if (_sysControl.SetBrightness(_hwnd, b))
            BrightnessValText.Text = $"{b}%";
    }

    private void TileProxyBtn_Click(object sender, RoutedEventArgs e)
    {
        bool enabled = _sysControl.ToggleSystemProxy(_settings);
        RefreshProxyTile();
        FooterLeftText.Text = enabled ? $"系统代理已开启 (端口 {_settings.SystemProxyPort})" : "系统代理已关闭";
    }

    private void TileProxyBtn_RightClick(object sender, MouseButtonEventArgs e)
    {
        _sysControl.LaunchOrShowProxyClient(_settings);
        e.Handled = true;
    }

    private async void TileBtBtn_Click(object sender, RoutedEventArgs e)
    {
        bool on = await _sysControl.ToggleBluetoothAsync();
        TileBtSub.Text = on ? "已开启 · 右键打开设置" : "已切换 / 查看设置";
    }

    private void TileBtBtn_RightClick(object sender, MouseButtonEventArgs e)
    {
        _sysControl.OpenBluetoothSettings();
        e.Handled = true;
    }

    private void TileCollapsedModeBtn_Click(object sender, RoutedEventArgs e)
    {
        CycleCollapsedMode(1);
        FooterLeftText.Text = $"收起态已切换为：{TileCollapsedModeSub.Text}";
    }

    private void TileFullscreenBtn_Click(object sender, RoutedEventArgs e)
    {
        _settings.HideOnFullscreen = !_settings.HideOnFullscreen;
        _settings.Save();
        UpdateDualScreenLabels();
    }

    private async void TileRefreshAllBtn_Click(object sender, RoutedEventArgs e)
    {
        FooterLeftText.Text = "正在全量刷新 AI 额度、外设电量与天气…";
        await Task.WhenAll(RefreshQuotasAsync(), RefreshBatteriesAsync(), RefreshWeatherAsync());
        FooterLeftText.Text = "全量刷新完成";
    }

    // ==================== RIGHT-CLICK CONTEXT MENU & TRAY ICON ====================

    private void BodyContainer_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (IsInsideInteractiveControl(e.OriginalSource as DependencyObject))
            return;
        OpenContextMenu();
        e.Handled = true;
    }

    private void SetupTrayIcon()
    {
        System.Drawing.Icon? icon = null;
        try
        {
            var icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(icoPath)) icon = new System.Drawing.Icon(icoPath);
        }
        catch { }

        _trayIcon = new Forms.NotifyIcon
        {
            Text = "IslandWidget · 双屏融合灵动岛",
            Icon = icon ?? System.Drawing.SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = BuildTrayMenu()
        };
        _trayIcon.DoubleClick += (_, _) =>
        {
            if (_settings.ClickThrough)
            {
                _settings.ClickThrough = false;
                DualMonitorHelper.SetClickThrough(this, false);
                _settings.Save();
            }
            ExpandIsland(pin: true);
        };
    }

    private void OpenContextMenu()
    {
        var menu = BuildTrayMenu();
        _contextMenuOpen = true;
        menu.Closed += (_, _) => _contextMenuOpen = false;
        menu.Show(Forms.Cursor.Position);
    }

    private Forms.ContextMenuStrip BuildTrayMenu()
    {
        var menu = new Forms.ContextMenuStrip { ShowImageMargin = true };

        var expandItem = new Forms.ToolStripMenuItem(_isExpanded ? "收起灵动岛" : "展开灵动岛");
        expandItem.Click += (_, _) => { if (_isExpanded) CollapseIsland(); else ExpandIsland(pin: true); };
        menu.Items.Add(expandItem);

        var switchMonItem = new Forms.ToolStripMenuItem("一键切换左主屏 / 右副屏");
        switchMonItem.Click += (_, _) => SwitchScreenBtn_Click(this, new RoutedEventArgs());
        menu.Items.Add(switchMonItem);

        var resetCenterItem = new Forms.ToolStripMenuItem("重置到当前右副屏顶部正中央");
        resetCenterItem.Click += (_, _) =>
        {
            _settings.MonitorTarget = "Secondary";
            _settings.DockEdge = "Top";
            _settings.EdgeRatio = 0.5;
            _settings.Save();
            UpdateDualScreenLabels();
            RepositionAndRedraw();
        };
        menu.Items.Add(resetCenterItem);

        menu.Items.Add(new Forms.ToolStripSeparator());

        var themeMenu = new Forms.ToolStripMenuItem("毛玻璃主题");
        foreach (var (key, label) in new[] { ("dark", "深色毛玻璃"), ("light", "浅色毛玻璃"), ("rouge", "胭脂撞色"), ("sky", "晴空渐变") })
        {
            var ti = new Forms.ToolStripMenuItem(label) { Checked = _settings.Theme == key };
            var captured = key;
            ti.Click += (_, _) => { _settings.Theme = captured; _settings.Save(); ApplyThemePalette(); };
            themeMenu.DropDownItems.Add(ti);
        }
        menu.Items.Add(themeMenu);

        var pinColItem = new Forms.ToolStripMenuItem("长关模式（不随悬停自动展开）") { Checked = _settings.PinnedCollapsed };
        pinColItem.Click += (_, _) => { _settings.PinnedCollapsed = !_settings.PinnedCollapsed; _settings.Save(); };
        menu.Items.Add(pinColItem);

        var clickThroughItem = new Forms.ToolStripMenuItem("鼠标穿透（托盘双击可恢复）") { Checked = _settings.ClickThrough };
        clickThroughItem.Click += (_, _) =>
        {
            _settings.ClickThrough = !_settings.ClickThrough;
            if (_settings.ClickThrough && _isExpanded) CollapseIsland();
            DualMonitorHelper.SetClickThrough(this, _settings.ClickThrough);
            _settings.Save();
            if (_trayIcon is not null) _trayIcon.ContextMenuStrip = BuildTrayMenu();
        };
        menu.Items.Add(clickThroughItem);

        menu.Items.Add(new Forms.ToolStripSeparator());

        var refreshItem = new Forms.ToolStripMenuItem("立即刷新 (额度/电量/天气)");
        refreshItem.Click += async (_, _) => await Task.WhenAll(RefreshQuotasAsync(), RefreshBatteriesAsync(), RefreshWeatherAsync());
        menu.Items.Add(refreshItem);

        var settingsItem = new Forms.ToolStripMenuItem("设置…");
        settingsItem.Click += (_, _) => ShowSettingsDialog();
        menu.Items.Add(settingsItem);

        menu.Items.Add(new Forms.ToolStripSeparator());

        var exitItem = new Forms.ToolStripMenuItem("退出 IslandWidget");
        exitItem.Click += (_, _) => Application.Current.Shutdown();
        menu.Items.Add(exitItem);

        return menu;
    }

    private void ShowSettingsDialog()
    {
        bool wasPinned = _pinnedExpanded;
        _pinnedExpanded = true;
        var dlg = new SettingsWindow(_settings, _weatherService);
        if (dlg.ShowDialog() == true)
        {
            var updated = dlg.ResultSettings;
            bool cityChanged = !string.Equals(_settings.WeatherCity, updated.WeatherCity, StringComparison.OrdinalIgnoreCase);

            _settings.MonitorTarget = updated.MonitorTarget;
            _settings.DockEdge = updated.DockEdge;
            if (dlg.ResetEdgeRatioRequested) _settings.EdgeRatio = 0.5;
            _settings.Theme = updated.Theme;
            _settings.GlassOpacity = updated.GlassOpacity;
            _settings.LiquidEffect = updated.LiquidEffect;
            _settings.ExpandOnHover = updated.ExpandOnHover;
            _settings.AutoShowLyrics = updated.AutoShowLyrics;
            _settings.IncludeBrowserVideoMedia = updated.IncludeBrowserVideoMedia;
            _settings.CompactShowQuotas = updated.CompactShowQuotas;
            _settings.CompactShowHardware = updated.CompactShowHardware;
            _settings.CompactShowBattery = updated.CompactShowBattery;
            _settings.CompactShowQuickActions = updated.CompactShowQuickActions;
            _settings.CompactShowClock = updated.CompactShowClock;
            _settings.HideOnFullscreen = updated.HideOnFullscreen;
            _settings.LowPowerMode = updated.LowPowerMode;
            _settings.PauseWhenLocked = updated.PauseWhenLocked;
            _settings.StartWithWindows = updated.StartWithWindows;
            _settings.QuotaRefreshMinutes = updated.QuotaRefreshMinutes;
            _settings.AutoDetectProxy = updated.AutoDetectProxy;
            _settings.ManualProxyUrl = updated.ManualProxyUrl;
            _settings.SystemProxyPort = updated.SystemProxyPort;
            _settings.WeatherCity = updated.WeatherCity;
            _settings.WeatherLat = updated.WeatherLat;
            _settings.WeatherLon = updated.WeatherLon;
            _settings.Save();

            _quotaTick.Interval = TimeSpan.FromMinutes(_settings.QuotaRefreshMinutes);
            ApplyThemePalette();
            UpdateCollapsedModeVisibility();
            if (cityChanged) _ = RefreshWeatherAsync();
            _ = RefreshQuotasAsync();
        }
        _pinnedExpanded = wasPinned;
        UpdatePinVisual();
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    private static uint GetUserIdleMs()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        return GetLastInputInfo(ref info) ? unchecked((uint)Environment.TickCount - info.dwTime) : uint.MaxValue;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
