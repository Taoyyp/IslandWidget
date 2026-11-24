using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace IslandWidget.Services;

public readonly record struct HardwareSnapshot(
    int CpuPercent,
    int RamPercent,
    double RamUsedGb,
    double RamTotalGb,
    int GpuPercent,
    int? GpuTempC,
    double GpuVramUsedGb,
    double GpuVramTotalGb,
    string GpuName,
    double NetDownloadBps,
    double NetUploadBps)
{
    public static string FormatRate(double bytesPerSec)
    {
        if (bytesPerSec < 1024)
            return $"{bytesPerSec:0}B/s";
        if (bytesPerSec < 1024 * 1024)
            return $"{bytesPerSec / 1024.0:0.#}K/s";
        return $"{bytesPerSec / (1024.0 * 1024.0):0.0}M/s";
    }
}

/// <summary>
/// Zero-WMI, near-zero allocation native Windows telemetry:
/// - CPU: kernel32!GetSystemTimes
/// - RAM: kernel32!GlobalMemoryStatusEx
/// - GPU: NVML (nvml.dll) / NVAPI (nvapi64.dll) for NVIDIA RTX 3060 + ADL fallback
/// - Net: iphlpapi!GetIfTable2
/// </summary>
public sealed class HardwareMonitorService : IDisposable
{
    private ulong _prevIdle, _prevKernel, _prevUser;
    private bool _hasCpuBaseline;

    private ulong _prevNetRx, _prevNetTx;
    private long _prevNetTick;
    private bool _hasNetBaseline;

    private readonly NvmlGpuReader _nvml = new();
    private readonly NvApiGpuReader _nvapi = new();
    private bool _disposed;

    public HardwareMonitorService()
    {
        // Prime baselines immediately
        SampleCpu();
        SampleNetwork();
    }

    public HardwareSnapshot Sample(bool includeGpuTemp = true)
    {
        var cpu = SampleCpu();
        var (ramPct, ramUsed, ramTotal) = SampleRam();
        var (gpuPct, gpuTemp, vramUsed, vramTotal, gpuName) = SampleGpu(includeGpuTemp);
        var (rxBps, txBps) = SampleNetwork();

        return new HardwareSnapshot(
            cpu, ramPct, ramUsed, ramTotal,
            gpuPct, gpuTemp, vramUsed, vramTotal, gpuName,
            rxBps, txBps);
    }

    private int SampleCpu()
    {
        if (!GetSystemTimes(out var idleFt, out var kernelFt, out var userFt))
            return 0;

        ulong idle = ToUInt64(idleFt);
        ulong kernel = ToUInt64(kernelFt);
        ulong user = ToUInt64(userFt);

        if (!_hasCpuBaseline)
        {
            _prevIdle = idle;
            _prevKernel = kernel;
            _prevUser = user;
            _hasCpuBaseline = true;
            return 0;
        }

        ulong dIdle = idle - _prevIdle;
        ulong dKernel = kernel - _prevKernel;
        ulong dUser = user - _prevUser;

        _prevIdle = idle;
        _prevKernel = kernel;
        _prevUser = user;

        ulong total = dKernel + dUser;
        if (total == 0 || total <= dIdle) return 0;
        var busy = (double)(total - dIdle) * 100.0 / total;
        return Math.Clamp((int)Math.Round(busy), 0, 100);
    }

    private static (int Percent, double UsedGb, double TotalGb) SampleRam()
    {
        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref mem) || mem.ullTotalPhys == 0)
            return (0, 0, 0);

        var totalGb = mem.ullTotalPhys / (1024.0 * 1024.0 * 1024.0);
        var usedGb = (mem.ullTotalPhys - mem.ullAvailPhys) / (1024.0 * 1024.0 * 1024.0);
        return (Math.Clamp((int)mem.dwMemoryLoad, 0, 100), usedGb, totalGb);
    }

    private (int Percent, int? TempC, double VramUsedGb, double VramTotalGb, string Name) SampleGpu(bool wantTemp)
    {
        if (_nvml.TryRead(wantTemp, out var pct, out var temp, out var usedGb, out var totalGb, out var name))
            return (pct, temp, usedGb, totalGb, name);

        if (_nvapi.TryRead(wantTemp, out pct, out temp))
            return (pct, temp, 0, 0, "NVIDIA GPU");

        return (0, null, 0, 0, "GPU");
    }

    private unsafe (double DownloadBps, double UploadBps) SampleNetwork()
    {
        IntPtr tablePtr = IntPtr.Zero;
        try
        {
            if (GetIfTable2(out tablePtr) != 0 || tablePtr == IntPtr.Zero)
                return (0, 0);

            int count = Marshal.ReadInt32(tablePtr);
            if (count <= 0 || count > 512) return (0, 0);

            // MIB_IF_TABLE2: ULONG NumEntries (4 bytes + 4 padding on x64 = offset 8), followed by MIB_IF_ROW2[count]
            // Size of MIB_IF_ROW2 on Windows x64 is 1352 bytes:
            //   NET_LUID InterfaceLuid (0, 8B)
            //   NET_IFINDEX InterfaceIndex (8, 4B)
            //   GUID InterfaceGuid (12, 16B)
            //   WCHAR Alias[257] (28, 514B)
            //   WCHAR Description[257] (542, 514B)
            //   ULONG PhysicalAddressLength (1056, 4B)
            //   UCHAR PhysicalAddress[32] (1060, 32B)
            //   UCHAR PermanentPhysicalAddress[32] (1092, 32B)
            //   ULONG Mtu (1124, 4B)
            //   IFTYPE Type (1128, 4B)
            //   TUNNEL_TYPE TunnelType (1132, 4B)
            //   NDIS_MEDIUM MediaType (1136, 4B)
            //   NDIS_PHYSICAL_MEDIUM PhysicalMediumType (1140, 4B)
            //   NET_IF_ACCESS_TYPE AccessType (1144, 4B)
            //   NET_IF_DIRECTION_TYPE DirectionType (1148, 4B)
            //   InterfaceAndOperStatusFlags (1152, 1B) -> bit0 HardwareInterface, bit1 FilterInterface
            //   IF_OPER_STATUS OperStatus (1156, 4B) -> 1 = IfOperStatusUp
            //   ...
            //   ULONG64 InOctets (1216, 8B)
            //   ...
            //   ULONG64 OutOctets (1280, 8B)
            const int RowOffset = 8;
            const int RowSize = 1352;

            ulong totalRx = 0, totalTx = 0;
            byte* basePtr = (byte*)tablePtr + RowOffset;

            for (int i = 0; i < count; i++)
            {
                byte* row = basePtr + i * RowSize;
                uint ifType = *(uint*)(row + 1128);
                int operStatus = *(int*)(row + 1156);
                byte flags = *(row + 1152);
                bool isFilter = (flags & 0x02) != 0;
                bool isHardware = (flags & 0x01) != 0;

                // Skip loopback (24), tunnel (131), non-up interfaces, or NDIS filter duplicates
                if (operStatus != 1 || ifType == 24 || ifType == 131 || isFilter)
                    continue;

                // Prefer real hardware NICs (Ethernet 6 / Wi-Fi 71) so virtual Clash TUN doesn't double-count traffic!
                if (!isHardware)
                    continue;

                ulong inOctets = *(ulong*)(row + 1216);
                ulong outOctets = *(ulong*)(row + 1280);
                totalRx += inOctets;
                totalTx += outOctets;
            }

            long nowTick = Stopwatch.GetTimestamp();
            if (!_hasNetBaseline)
            {
                _prevNetRx = totalRx;
                _prevNetTx = totalTx;
                _prevNetTick = nowTick;
                _hasNetBaseline = true;
                return (0, 0);
            }

            double dt = (double)(nowTick - _prevNetTick) / Stopwatch.Frequency;
            if (dt <= 0.05) return (0, 0);

            double rxRate = totalRx >= _prevNetRx ? (totalRx - _prevNetRx) / dt : 0;
            double txRate = totalTx >= _prevNetTx ? (totalTx - _prevNetTx) / dt : 0;

            _prevNetRx = totalRx;
            _prevNetTx = totalTx;
            _prevNetTick = nowTick;

            return (rxRate, txRate);
        }
        finally
        {
            if (tablePtr != IntPtr.Zero)
                FreeMibTable(tablePtr);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _nvml.Dispose();
    }

    private static ulong ToUInt64(System.Runtime.InteropServices.ComTypes.FILETIME ft)
        => ((ulong)(uint)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(
        out System.Runtime.InteropServices.ComTypes.FILETIME lpIdleTime,
        out System.Runtime.InteropServices.ComTypes.FILETIME lpKernelTime,
        out System.Runtime.InteropServices.ComTypes.FILETIME lpUserTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("iphlpapi.dll")]
    private static extern int GetIfTable2(out IntPtr table);

    [DllImport("iphlpapi.dll")]
    private static extern void FreeMibTable(IntPtr memory);

    /// <summary>
    /// Direct NVML reader (nvml.dll in System32 or NVIDIA DriverStore) — zero-process, sub-0.05ms GPU utilization + VRAM + Temp.
    /// </summary>
    private sealed class NvmlGpuReader : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct NvmlUtilization { public uint Gpu; public uint Memory; }

        [StructLayout(LayoutKind.Sequential)]
        private struct NvmlMemory { public ulong Total; public ulong Free; public ulong Used; }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlInitDel();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlShutdownDel();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlGetHandleDel(uint index, out IntPtr device);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlGetNameDel(IntPtr device, byte[] name, uint length);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlGetUtilDel(IntPtr device, out NvmlUtilization util);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlGetMemDel(IntPtr device, out NvmlMemory mem);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlGetTempDel(IntPtr device, uint sensorType, out uint temp);

        private bool _tried;
        private bool _ready;
        private IntPtr _lib;
        private IntPtr _device;
        private string _gpuName = "NVIDIA GPU";
        private NvmlShutdownDel? _shutdown;
        private NvmlGetUtilDel? _getUtil;
        private NvmlGetMemDel? _getMem;
        private NvmlGetTempDel? _getTemp;

        public bool TryRead(bool wantTemp, out int pct, out int? tempC, out double usedGb, out double totalGb, out string name)
        {
            pct = 0; tempC = null; usedGb = 0; totalGb = 0; name = _gpuName;
            if (!_tried)
            {
                _tried = true;
                _ready = Init();
            }
            if (!_ready) return false;

            if (_getUtil!(_device, out var util) != 0)
                return false;

            pct = Math.Clamp((int)util.Gpu, 0, 100);
            if (_getMem is not null && _getMem(_device, out var mem) == 0 && mem.Total > 0)
            {
                totalGb = mem.Total / (1024.0 * 1024.0 * 1024.0);
                usedGb = mem.Used / (1024.0 * 1024.0 * 1024.0);
            }
            if (wantTemp && _getTemp is not null && _getTemp(_device, 0, out var t) == 0 && t is > 0 and < 130)
            {
                tempC = (int)t;
            }
            name = _gpuName;
            return true;
        }

        private bool Init()
        {
            try
            {
                if (!NativeLibrary.TryLoad("nvml.dll", typeof(NvmlGpuReader).Assembly, DllImportSearchPath.System32, out _lib))
                {
                    var pf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvml.dll");
                    if (!File.Exists(pf) || !NativeLibrary.TryLoad(pf, out _lib))
                        return false;
                }

                var init = Export<NvmlInitDel>("nvmlInit_v2") ?? Export<NvmlInitDel>("nvmlInit");
                if (init is null || init() != 0) return false;

                _shutdown = Export<NvmlShutdownDel>("nvmlShutdown");
                var getHandle = Export<NvmlGetHandleDel>("nvmlDeviceGetHandleByIndex_v2") ?? Export<NvmlGetHandleDel>("nvmlDeviceGetHandleByIndex");
                _getUtil = Export<NvmlGetUtilDel>("nvmlDeviceGetUtilizationRates");
                _getMem = Export<NvmlGetMemDel>("nvmlDeviceGetMemoryInfo");
                _getTemp = Export<NvmlGetTempDel>("nvmlDeviceGetTemperature");

                if (getHandle is null || _getUtil is null || getHandle(0, out _device) != 0 || _device == IntPtr.Zero)
                    return false;

                var getName = Export<NvmlGetNameDel>("nvmlDeviceGetName");
                if (getName is not null)
                {
                    var buf = new byte[96];
                    if (getName(_device, buf, (uint)buf.Length) == 0)
                    {
                        var end = Array.IndexOf(buf, (byte)0);
                        var raw = System.Text.Encoding.ASCII.GetString(buf, 0, end >= 0 ? end : buf.Length).Trim();
                        if (!string.IsNullOrWhiteSpace(raw))
                            _gpuName = raw.Replace("NVIDIA GeForce ", "", StringComparison.OrdinalIgnoreCase);
                    }
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private T? Export<T>(string name) where T : Delegate
            => _lib != IntPtr.Zero && NativeLibrary.TryGetExport(_lib, name, out var ptr)
                ? Marshal.GetDelegateForFunctionPointer<T>(ptr)
                : null;

        public void Dispose()
        {
            try { if (_ready && _shutdown is not null) _shutdown(); } catch { }
            _ready = false;
        }
    }

    /// <summary>
    /// Fallback NVAPI reader (nvapi64.dll) in case nvml.dll is unavailable.
    /// </summary>
    private sealed class NvApiGpuReader
    {
        private const uint IdInit = 0x0150E828, IdEnum = 0xE5AC921F, IdPstates = 0x60DED2ED, IdThermal = 0xE3640A56;
        private const int PstatesInts = 18, PstatesVer = PstatesInts * 4 | (1 << 16);
        private const int ThermalInts = 17, ThermalVer = ThermalInts * 4 | (2 << 16);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr QueryInterfaceDel(uint id);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InitDel();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int EnumDel([Out] IntPtr[] handles, out int count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int PstatesDel(IntPtr gpu, [In, Out] int[] info);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ThermalDel(IntPtr gpu, int sensor, [In, Out] int[] settings);

        private bool _tried, _ready;
        private IntPtr _gpu;
        private PstatesDel? _pstates;
        private ThermalDel? _thermal;
        private readonly int[] _info = new int[PstatesInts];
        private readonly int[] _therm = new int[ThermalInts];

        public bool TryRead(bool wantTemp, out int pct, out int? tempC)
        {
            pct = 0; tempC = null;
            if (!_tried)
            {
                _tried = true;
                try
                {
                    if (NativeLibrary.TryLoad("nvapi64.dll", typeof(NvApiGpuReader).Assembly, DllImportSearchPath.System32, out var lib) &&
                        NativeLibrary.TryGetExport(lib, "nvapi_QueryInterface", out var qPtr))
                    {
                        var query = Marshal.GetDelegateForFunctionPointer<QueryInterfaceDel>(qPtr);
                        T? Fn<T>(uint id) where T : Delegate => query(id) is var p && p != IntPtr.Zero ? Marshal.GetDelegateForFunctionPointer<T>(p) : null;
                        var init = Fn<InitDel>(IdInit);
                        var en = Fn<EnumDel>(IdEnum);
                        _pstates = Fn<PstatesDel>(IdPstates);
                        _thermal = Fn<ThermalDel>(IdThermal);
                        var handles = new IntPtr[64];
                        if (init is not null && init() == 0 && en is not null && _pstates is not null && en(handles, out var cnt) == 0 && cnt > 0)
                        {
                            _gpu = handles[0];
                            _ready = true;
                        }
                    }
                }
                catch { }
            }
            if (!_ready) return false;

            Array.Clear(_info);
            _info[0] = PstatesVer;
            if (_pstates!(_gpu, _info) != 0) return false;
            pct = (_info[2] & 1) != 0 ? Math.Clamp(_info[3], 0, 100) : 0;

            if (wantTemp && _thermal is not null)
            {
                Array.Clear(_therm);
                _therm[0] = ThermalVer;
                if (_thermal(_gpu, 15, _therm) == 0)
                {
                    for (int i = 0; i < Math.Min(_therm[1], 3); i++)
                    {
                        if (_therm[2 + i * 5 + 4] == 1)
                        {
                            tempC = _therm[2 + i * 5 + 3];
                            break;
                        }
                    }
                }
            }
            return true;
        }
    }
}
