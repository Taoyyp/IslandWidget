using System.Diagnostics;
using System.IO;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Windows;
using IslandWidget.Models;
using IslandWidget.Services;
using IslandWidget.UI;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace IslandWidget;

public partial class App : Application
{
    private const string MutexName = @"Local\IslandWidget.SingleInstance";
    private const string ActivateEventName = @"Local\IslandWidget.Activate";

    private Mutex? _mutex;
    private EventWaitHandle? _activateEvent;
    private RegisteredWaitHandle? _activationWait;
    private volatile bool _exiting;

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr proc, nint min, nint max);

    public App()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            LogFatal(args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogFatal(args.ExceptionObject as Exception);
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            await RunSelfTestAsync();
            Environment.Exit(0);
            return;
        }

        var demo = e.Args.Contains("--demo", StringComparer.OrdinalIgnoreCase);
        var suffix = demo ? ".Demo" : "";
        _mutex = new Mutex(true, MutexName + suffix, out var createdNew);
        if (!createdNew)
        {
            SignalRunningInstance(suffix);
            _mutex.Dispose();
            _mutex = null;
            Shutdown();
            return;
        }

        try
        {
            // Keep background priority gentle so games/IDEs always preempt us
            try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }

            _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName + suffix, out _);
            StartActivationListener();

            var settings = AppSettings.Load();
            var window = new MainWindow(settings, demo);
            MainWindow = window;
            window.Show();

            if (e.Args.Contains("--expand", StringComparer.OrdinalIgnoreCase))
            {
                window.ExpandIsland(pin: true);
            }
        }
        catch (Exception ex)
        {
            LogFatal(ex);
            MessageBox.Show(
                "灵动岛启动失败：\n" + ex.Message,
                "IslandWidget",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
        }
    }

    public static void TrimMemory()
    {
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Optimized, blocking: false, compacting: true);
            SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, -1, -1);
        }
        catch
        {
            // Best-effort memory trim when collapsed
        }
    }

    public static void LogFatal(Exception? ex)
    {
        if (ex is null) return;
        var paths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IslandWidget", "error.log"),
            Path.Combine(AppContext.BaseDirectory, "error.log")
        };
        foreach (var path in paths)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}{Environment.NewLine}");
                return;
            }
            catch { }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true;
        _activationWait?.Unregister(null);
        _activationWait = null;
        _activateEvent?.Dispose();
        _activateEvent = null;

        if (MainWindow is MainWindow win)
            win.ShutdownCleanly();

        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        _mutex = null;
        base.OnExit(e);
    }

    private void StartActivationListener()
    {
        if (_activateEvent is null) return;
        _activationWait = ThreadPool.RegisterWaitForSingleObject(_activateEvent, (_, _) =>
        {
            if (_exiting || Dispatcher.HasShutdownStarted) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (!_exiting && MainWindow is MainWindow win)
                    win.ExpandIsland(pin: false);
            });
        }, null, Timeout.Infinite, executeOnlyOnce: false);
    }

    private static async Task RunSelfTestAsync()
    {
        var tracePath = Path.Combine(AppContext.BaseDirectory, "selftest_trace.log");
        void Trace(string msg) => File.AppendAllText(tracePath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}{Environment.NewLine}");
        try
        {
            File.WriteAllText(tracePath, "");
            Trace("Start Load Settings");
            var settings = AppSettings.Load();
            Trace("Start HardwareMonitorService");
            using var hw = new HardwareMonitorService();
            await Task.Delay(250);
            var hwSnap = hw.Sample(includeGpuTemp: true);
            Trace($"HW OK: CPU={hwSnap.CpuPercent}% RAM={hwSnap.RamPercent}% GPU={hwSnap.GpuName} {hwSnap.GpuPercent}% Temp={hwSnap.GpuTempC}");

            Trace("Start WirelessBatteryService");
            using var bat = new WirelessBatteryService();
            var devices = await bat.RefreshAsync(demoMode: false);
            Trace($"Battery OK: count={devices.Count}");

            Trace("Start NativeQuotaService");
            using var quota = new NativeQuotaService();
            using var qCts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            var quotas = await quota.FetchAllAsync(settings, demoMode: false, qCts.Token);
            Trace($"Quota OK: AGY={quotas.Antigravity.Ok} CDX={quotas.Codex.Ok} DSH={quotas.Dsh.Ok}");

            Trace("Start WeatherService");
            using var weather = new WeatherService();
            using var wCts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            var wSnap = await weather.RefreshAsync(settings, demoMode: false, wCts.Token);
            Trace($"Weather OK: {wSnap.Ok} {wSnap.TempC}C");

            Trace("Start SystemControlService");
            var sys = new SystemControlService();
            var (vol, muted) = sys.GetMasterAudio();
            var proxy = sys.GetProxyStatus();
            var targetScreen = DualMonitorHelper.ResolveScreen(settings.MonitorTarget);
            Trace("All services sampled");

            var report = new
            {
                TargetScreen = new { targetScreen.DeviceName, targetScreen.Primary, Bounds = targetScreen.Bounds.ToString(), WorkingArea = targetScreen.WorkingArea.ToString() },
                Hardware = hwSnap,
                WirelessDevices = devices,
                Quotas = new
                {
                    Antigravity = new { quotas.Antigravity.Ok, quotas.Antigravity.CompactSummary, quotas.Antigravity.Subtitle, quotas.Antigravity.Error },
                    Codex = new { quotas.Codex.Ok, quotas.Codex.CompactSummary, quotas.Codex.Subtitle, quotas.Codex.Error },
                    Dsh = new { quotas.Dsh.Ok, quotas.Dsh.CompactSummary, quotas.Dsh.Balance, quotas.Dsh.Bonus, quotas.Dsh.Spent, quotas.Dsh.Error }
                },
                Weather = new { wSnap.Ok, wSnap.City, wSnap.TempC, wSnap.Desc, wSnap.Humidity, wSnap.Error },
                Audio = new { vol, muted },
                Proxy = new { proxy.ClashRunning, proxy.SysProxyEnabled, proxy.ProxyServer, settings.FlClashPath }
            };

            var outPath = Path.Combine(AppContext.BaseDirectory, "selftest.json");
            File.WriteAllText(outPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            LogFatal(ex);
        }
    }

    private static void SignalRunningInstance(string suffix)
    {
        try
        {
            using var handle = EventWaitHandle.OpenExisting(ActivateEventName + suffix);
            handle.Set();
        }
        catch { }
    }
}
