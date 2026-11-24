using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Media;

namespace IslandWidget.UI;

public static class DualMonitorHelper
{
    public static IReadOnlyList<Screen> GetAllScreens() => Screen.AllScreens;

    /// <summary>
    /// Resolves the target monitor:
    /// - "Secondary": picks first non-Primary monitor (e.g. Right Monitor DISPLAY1), falling back to Primary if single screen
    /// - "Primary": picks Primary monitor (e.g. Left Monitor DISPLAY2)
    /// - DeviceName: exact match if still connected, else Secondary/Primary fallback
    /// </summary>
    public static Screen ResolveScreen(string monitorTarget)
    {
        var screens = Screen.AllScreens;
        if (screens.Length == 0)
            return Screen.PrimaryScreen!;

        if (string.Equals(monitorTarget, "Secondary", StringComparison.OrdinalIgnoreCase))
        {
            return screens.FirstOrDefault(s => !s.Primary) ?? Screen.PrimaryScreen ?? screens[0];
        }

        if (string.Equals(monitorTarget, "Primary", StringComparison.OrdinalIgnoreCase))
        {
            return Screen.PrimaryScreen ?? screens[0];
        }

        return screens.FirstOrDefault(s => string.Equals(s.DeviceName, monitorTarget, StringComparison.OrdinalIgnoreCase))
               ?? screens.FirstOrDefault(s => !s.Primary)
               ?? Screen.PrimaryScreen
               ?? screens[0];
    }

    public static string DescribeScreen(Screen screen, int index)
    {
        var tag = screen.Primary ? "主屏" : "副屏";
        var side = Screen.AllScreens.Length > 1
            ? (screen.Bounds.X >= (Screen.PrimaryScreen?.Bounds.Right ?? 1920) - 10 ? "右" :
               screen.Bounds.Right <= (Screen.PrimaryScreen?.Bounds.Left ?? 0) + 10 ? "左" : "")
            : "";
        return $"屏幕 {index + 1} ({side}{tag} {screen.Bounds.Width}×{screen.Bounds.Height})";
    }

    /// <summary>
    /// Converts physical screen rectangle (pixels) into WPF device-independent coordinates (DIPs) for the given window.
    /// </summary>
    public static Rect PhysicalToDipRect(Window window, System.Drawing.Rectangle phys)
    {
        var source = PresentationSource.FromVisual(window);
        if (source?.CompositionTarget is { } ct)
        {
            var m = ct.TransformFromDevice;
            var tl = m.Transform(new System.Windows.Point(phys.Left, phys.Top));
            var br = m.Transform(new System.Windows.Point(phys.Right, phys.Bottom));
            return new Rect(tl, br);
        }
        var dpi = VisualTreeHelper.GetDpi(window);
        double sx = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
        double sy = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;
        return new Rect(phys.Left / sx, phys.Top / sy, phys.Width / sx, phys.Height / sy);
    }

    public static System.Windows.Point PhysicalToDipPoint(Window window, System.Drawing.Point pt)
    {
        var source = PresentationSource.FromVisual(window);
        if (source?.CompositionTarget is { } ct)
            return ct.TransformFromDevice.Transform(new System.Windows.Point(pt.X, pt.Y));
        var dpi = VisualTreeHelper.GetDpi(window);
        return new System.Windows.Point(pt.X / Math.Max(1.0, dpi.DpiScaleX), pt.Y / Math.Max(1.0, dpi.DpiScaleY));
    }

    /// <summary>
    /// Checks whether the foreground window is running fullscreen on the specified monitor (games, fullscreen video/PPT).
    /// </summary>
    public static (bool IsFullscreen, bool IsMaximized) CheckForegroundOnScreen(Screen targetScreen, IntPtr ownHwnd)
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == ownHwnd || !IsWindowVisible(fg))
            return (false, false);

        // Ignore desktop shell windows (Progman, WorkerW, Shell_TrayWnd)
        var cls = GetClassNameSafe(fg);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            return (false, false);

        if (!GetWindowRect(fg, out var r))
            return (false, false);

        var b = targetScreen.Bounds;
        var w = targetScreen.WorkingArea;

        // Check if foreground window center is on targetScreen
        int cx = (r.Left + r.Right) / 2;
        int cy = (r.Top + r.Bottom) / 2;
        if (!b.Contains(cx, cy))
            return (false, false);

        bool coversFull = r.Left <= b.Left + 2 && r.Top <= b.Top + 2 &&
                          r.Right >= b.Right - 2 && r.Bottom >= b.Bottom - 2;
        if (coversFull)
            return (true, true);

        bool coversWork = IsZoomed(fg) || (r.Left <= w.Left + 4 && r.Top <= w.Top + 4 && r.Right >= w.Right - 4);
        return (false, coversWork);
    }

    public static void SetClickThrough(Window window, bool enable)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        const int GWL_EXSTYLE = -20;
        const int WS_EX_TRANSPARENT = 0x00000020;
        const int WS_EX_TOOLWINDOW = 0x00000080;

        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        ex |= WS_EX_TOOLWINDOW;
        if (enable) ex |= WS_EX_TRANSPARENT;
        else ex &= ~WS_EX_TRANSPARENT;
        SetWindowLong(hwnd, GWL_EXSTYLE, ex);
    }

    private static string GetClassNameSafe(IntPtr hwnd)
    {
        var sb = new StringBuilder(64);
        int n = GetClassName(hwnd, sb, sb.Capacity);
        return n > 0 ? sb.ToString() : "";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
