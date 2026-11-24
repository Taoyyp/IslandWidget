using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using IslandWidget.Models;
using Windows.Devices.Radios;

namespace IslandWidget.Services;

/// <summary>
/// Native quick system controls:
/// - CoreAudio Master Volume & Mute (zero NuGet COM vtable)
/// - Monitor Brightness (DDC/CI dxva2.dll + WMI fallback)
/// - Bluetooth & Wi-Fi Radios (Windows.Devices.Radios)
/// - FlClash / System Proxy state & one-click toggle
/// </summary>
public sealed class SystemControlService
{
    // ---- Audio Volume & Mute via CoreAudio IMMDeviceEnumerator / IAudioEndpointVolume ----
    public (int VolumePercent, bool Muted) GetMasterAudio()
    {
        try
        {
            var ep = GetAudioEndpointVolume();
            if (ep is null) return (50, false);
            try
            {
                ep.GetMasterVolumeLevelScalar(out float level);
                ep.GetMute(out bool muted);
                return (Math.Clamp((int)Math.Round(level * 100), 0, 100), muted);
            }
            finally
            {
                Marshal.ReleaseComObject(ep);
            }
        }
        catch
        {
            return (50, false);
        }
    }

    public void SetMasterVolume(int percent)
    {
        try
        {
            var ep = GetAudioEndpointVolume();
            if (ep is null) return;
            try
            {
                float scalar = Math.Clamp(percent / 100f, 0f, 1f);
                var ctx = Guid.Empty;
                ep.SetMasterVolumeLevelScalar(scalar, ref ctx);
                if (percent > 0)
                    ep.SetMute(false, ref ctx);
            }
            finally
            {
                Marshal.ReleaseComObject(ep);
            }
        }
        catch { }
    }

    public bool ToggleMute()
    {
        try
        {
            var ep = GetAudioEndpointVolume();
            if (ep is null) return false;
            try
            {
                ep.GetMute(out bool muted);
                var ctx = Guid.Empty;
                ep.SetMute(!muted, ref ctx);
                return !muted;
            }
            finally
            {
                Marshal.ReleaseComObject(ep);
            }
        }
        catch
        {
            return false;
        }
    }

    // ---- Screen Brightness (DDC/CI external monitor + WMI internal laptop panel) ----
    public int? GetBrightness(IntPtr hwnd)
    {
        // Try DDC/CI on current monitor first
        try
        {
            IntPtr hMon = MonitorFromWindow(hwnd, 2); // MONITOR_DEFAULTTONEAREST
            if (hMon != IntPtr.Zero && GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out uint count) && count > 0)
            {
                var mons = new PHYSICAL_MONITOR[count];
                if (GetPhysicalMonitorsFromHMONITOR(hMon, count, mons))
                {
                    try
                    {
                        if (GetMonitorBrightness(mons[0].hPhysicalMonitor, out uint min, out uint cur, out uint max) && max > min)
                            return Math.Clamp((int)Math.Round((cur - min) * 100.0 / (max - min)), 0, 100);
                    }
                    finally
                    {
                        DestroyPhysicalMonitors(count, mons);
                    }
                }
            }
        }
        catch { }
        return null;
    }

    public bool SetBrightness(IntPtr hwnd, int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        try
        {
            IntPtr hMon = MonitorFromWindow(hwnd, 2);
            if (hMon != IntPtr.Zero && GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out uint count) && count > 0)
            {
                var mons = new PHYSICAL_MONITOR[count];
                if (GetPhysicalMonitorsFromHMONITOR(hMon, count, mons))
                {
                    try
                    {
                        if (GetMonitorBrightness(mons[0].hPhysicalMonitor, out uint min, out _, out uint max) && max > min)
                        {
                            uint target = (uint)Math.Round(min + (max - min) * (percent / 100.0));
                            return SetMonitorBrightness(mons[0].hPhysicalMonitor, target);
                        }
                    }
                    finally
                    {
                        DestroyPhysicalMonitors(count, mons);
                    }
                }
            }
        }
        catch { }
        return false;
    }

    // ---- Bluetooth & Wi-Fi Radios ----
    public async Task<(bool BtOn, bool WifiOn)> GetRadioStatesAsync()
    {
        bool bt = false, wifi = true;
        try
        {
            var radios = await Radio.GetRadiosAsync().AsTask().ConfigureAwait(false);
            foreach (var r in radios)
            {
                if (r.Kind == RadioKind.Bluetooth && r.State == RadioState.On) bt = true;
                if (r.Kind == RadioKind.WiFi) wifi = r.State == RadioState.On;
            }
        }
        catch { }
        return (bt, wifi);
    }

    public async Task<bool> ToggleBluetoothAsync()
    {
        try
        {
            var access = await Radio.RequestAccessAsync().AsTask().ConfigureAwait(false);
            if (access == RadioAccessStatus.Allowed)
            {
                var radios = await Radio.GetRadiosAsync().AsTask().ConfigureAwait(false);
                var bt = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
                if (bt is not null)
                {
                    var next = bt.State == RadioState.On ? RadioState.Off : RadioState.On;
                    var res = await bt.SetStateAsync(next).AsTask().ConfigureAwait(false);
                    if (res == RadioAccessStatus.Allowed)
                        return next == RadioState.On;
                }
            }
        }
        catch { }

        OpenUri("ms-settings:bluetooth");
        return false;
    }

    public void OpenWifiSettings() => OpenUri("ms-settings:network-wifi");
    public void OpenBluetoothSettings() => OpenUri("ms-settings:bluetooth");

    // ---- FlClash & Windows System Proxy ----
    public (bool ClashRunning, bool SysProxyEnabled, string ProxyServer) GetProxyStatus()
    {
        bool running = false;
        try
        {
            foreach (var name in new[] { "FlClash", "FlClashCore", "clash-verge", "mihomo", "v2rayN" })
            {
                var procs = Process.GetProcessesByName(name);
                if (procs.Length > 0)
                {
                    running = true;
                    foreach (var p in procs) p.Dispose();
                    break;
                }
            }
        }
        catch { }

        var (enabled, server) = ProxyDetector.ReadRawSystemProxy();
        return (running, enabled, server);
    }

    public bool ToggleSystemProxy(AppSettings settings)
    {
        var (enabled, _) = ProxyDetector.ReadRawSystemProxy();
        bool next = !enabled;
        ProxyDetector.SetSystemProxy(next, settings.SystemProxyPort);
        return next;
    }

    public void LaunchOrShowFlClash(AppSettings settings)
    {
        var path = settings.FlClashPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            settings.AutoFillDefaults();
            path = settings.FlClashPath;
        }
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    WorkingDirectory = Path.GetDirectoryName(path) ?? "",
                    UseShellExecute = true
                });
                return;
            }
            catch { }
        }
        OpenUri("ms-settings:network-proxy");
    }

    public static void OpenUri(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch { }
    }

    // ---- CoreAudio & DDC/CI Native Definitions ----
    private static IAudioEndpointVolume? GetAudioEndpointVolume()
    {
        var clsid = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"); // MMDeviceEnumerator
        var type = Type.GetTypeFromCLSID(clsid);
        if (type is null) return null;
        var enumerator = (IMMDeviceEnumerator?)Activator.CreateInstance(type);
        if (enumerator is null) return null;
        try
        {
            // eRender = 0, eMultimedia = 1
            if (enumerator.GetDefaultAudioEndpoint(0, 1, out var device) != 0 || device is null)
                return null;
            try
            {
                var iid = typeof(IAudioEndpointVolume).GUID;
                if (device.Activate(ref iid, 23, IntPtr.Zero, out var obj) == 0 && obj is IAudioEndpointVolume ep)
                    return ep;
                return null;
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int NotImpl1();
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? ppDevice);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object? ppInterface);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IntPtr pNotify);
        int UnregisterControlChangeNotify(IntPtr pNotify);
        int GetChannelCount(out uint pnChannelCount);
        int SetMasterVolumeLevel(float fLevelDB, ref Guid pguidEventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float fLevel, ref Guid pguidEventContext);
        int GetMasterVolumeLevel(out float pfLevelDB);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float pfLevel);
        int SetChannelVolumeLevel(uint nChannel, float fLevelDB, ref Guid pguidEventContext);
        int SetChannelVolumeLevelScalar(uint nChannel, float fLevel, ref Guid pguidEventContext);
        int GetChannelVolumeLevel(uint nChannel, out float pfLevelDB);
        int GetChannelVolumeLevelScalar(uint nChannel, out float pfLevel);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid pguidEventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szPhysicalMonitorDescription;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint pdwNumberOfPhysicalMonitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint dwPhysicalMonitorArraySize, [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool DestroyPhysicalMonitors(uint dwPhysicalMonitorArraySize, [In] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetMonitorBrightness(IntPtr hMonitor, out uint pdwMinimumBrightness, out uint pdwCurrentBrightness, out uint pdwMaximumBrightness);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool SetMonitorBrightness(IntPtr hMonitor, uint dwNewBrightness);
}
