using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.RegularExpressions;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

namespace IslandWidget.Services;

public enum WirelessDeviceKind
{
    Mouse,
    Keyboard,
    Headset,
    Gamepad,
    Receiver
}

public sealed record WirelessDeviceStatus(
    string Key,
    string Name,
    WirelessDeviceKind Kind,
    string ConnectionType, // "2.4G", "蓝牙", "XInput"
    int? BatteryPercent,
    bool Charging = false,
    bool Asleep = false)
{
    public string IconGlyph => Kind switch
    {
        WirelessDeviceKind.Mouse => "\uE962",    // Mouse
        WirelessDeviceKind.Keyboard => "\uE765", // Keyboard
        WirelessDeviceKind.Headset => "\uE7F6",  // Headphone
        WirelessDeviceKind.Gamepad => "\uE7FC",  // Game
        _ => "\uE88E"                            // USB / Receiver
    };

    public string DisplayBattery => BatteryPercent.HasValue
        ? (Charging ? $"⚡{BatteryPercent.Value}%" : (Asleep ? $"{BatteryPercent.Value}% (休眠)" : $"{BatteryPercent.Value}%"))
        : (Asleep ? "休眠中" : "在线 (未报告电量)");
}

/// <summary>
/// Non-intrusive, read-only wireless peripheral battery reader:
/// 1. 2.4G Compx HID (0x3554 / 0x373B / 0x3710 — directly supports VXE Mouse 1K Dongle 3554:F58E, VGN, ATK, Pulsar, Scyrox, etc.)
/// 2. 2.4G AULA/VGN/ATK Keyboards (3554:FA09, 320F, 373B) & Logitech HID++ (0x046D) & Razer (0x1532)
/// 3. Standard HID Feature Battery Usages (0x06/0x20, 0x85/0x65, 0x85/0x66, 0x0D/0x3B) + 2.4G Receiver presence (e.g. 25A7:FA70)
/// 4. Windows Bluetooth PnP DEVPKEY_Bluetooth_Battery + BLE GATT Battery Service (0x180F)
/// 5. Xbox / XInput Wireless Controller battery
/// </summary>
public sealed class WirelessBatteryService : IDisposable
{
    private const string BtBatteryPropKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    private const string AepConnected = "System.Devices.Aep.IsConnected";
    private const string AepAddress = "System.Devices.Aep.DeviceAddress";
    private const string AepCategory = "System.Devices.Aep.Category";

    private readonly Dictionary<string, WirelessDeviceStatus> _lastKnown = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private bool _disposed;

    public IReadOnlyList<WirelessDeviceStatus> CurrentDevices { get; private set; } = [];
    public event Action<IReadOnlyList<WirelessDeviceStatus>>? DevicesUpdated;

    public async Task<IReadOnlyList<WirelessDeviceStatus>> RefreshAsync(bool demoMode = false, CancellationToken ct = default)
    {
        if (_disposed) return CurrentDevices;
        if (demoMode)
        {
            var demo = new List<WirelessDeviceStatus>
            {
                new("demo:mouse", "VXE 蜻蜓无线鼠标", WirelessDeviceKind.Mouse, "2.4G", 85, false),
                new("demo:kb", "2.4G 无线机械键盘", WirelessDeviceKind.Keyboard, "2.4G", 72, false),
                new("demo:bt", "蓝牙降噪耳机", WirelessDeviceKind.Headset, "蓝牙", 90, true)
            };
            CurrentDevices = demo;
            DevicesUpdated?.Invoke(demo);
            return demo;
        }

        if (!await _scanLock.WaitAsync(0, ct).ConfigureAwait(false))
            return CurrentDevices;

        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linkedCts.CancelAfter(TimeSpan.FromMilliseconds(4200));
            var token = linkedCts.Token;

            var hidList = new List<WirelessDeviceStatus>();
            try
            {
                await Task.Run(() => ReadHid24GDevices(hidList), token).WaitAsync(TimeSpan.FromMilliseconds(1600), ct).ConfigureAwait(false);
            }
            catch { }

            var btList = new List<WirelessDeviceStatus>();
            try
            {
                await ReadBluetoothDevicesAsync(btList, token).WaitAsync(TimeSpan.FromMilliseconds(3500), ct).ConfigureAwait(false);
            }
            catch { }

            var xinputList = new List<WirelessDeviceStatus>();
            try
            {
                ReadXInputControllers(xinputList);
            }
            catch { }

            var result = new List<WirelessDeviceStatus>(hidList.Count + btList.Count + xinputList.Count);
            result.AddRange(hidList);
            result.AddRange(btList);
            result.AddRange(xinputList);

            result.Sort((a, b) =>
            {
                int hasA = a.BatteryPercent.HasValue ? 0 : 1;
                int hasB = b.BatteryPercent.HasValue ? 0 : 1;
                if (hasA != hasB) return hasA.CompareTo(hasB);
                return a.Kind.CompareTo(b.Kind);
            });

            CurrentDevices = result;
            DevicesUpdated?.Invoke(result);
            return result;
        }
        finally
        {
            _scanLock.Release();
        }
    }

    private void ReadHid24GDevices(List<WirelessDeviceStatus> results)
    {
        var paths = EnumerateHidPaths();
        var seenGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var receiverFallbacks = new Dictionary<string, (int Vid, int Pid, string Name, WirelessDeviceKind Kind)>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            if (IsBluetoothHid(path)) continue;
            if (path.Contains("vhf", StringComparison.OrdinalIgnoreCase)) continue;
            var (vid, pid) = ParseVidPid(path);
            if (vid == 0 && pid == 0) continue;

            // Skip motherboard RGB / onboard audio MCUs (e.g. MSI 1462/0DB0, Asus Aura 0B05:19AF, Gigabyte 048D, C-Media/GeneralPlus 1B3F)
            if (vid is 0x1462 or 0x0DB0 or 0x1B3F or 0x048D or 0x26CE)
                continue;

            // Never call synchronous HidD_GetProductString (can block indefinitely on sleeping 2.4G dongles)
            var info = QueryHidCollection(path, readProductString: false);
            if (info is null) continue;

            string groupKey = GroupKey(path, vid, pid);

            // 1. Compx 17-byte 2.4G Mouse protocol (0x3554 VXE/VGN/ATK/MCHOSE/Scyrox/AttackShark, 0x373B ATK/VXE, 0x3710 Pulsar)
            if (UsagePageMatches(info.UsagePage, 0xFF02) && info.Usage == 0x0002 && info.InputLength == 17 && info.OutputLength == 17 &&
                (vid is 0x3554 or 0x373B or 0x3710 or 0x260D or 0x391D) && !seenGroups.Contains(groupKey))
            {
                var devKey = $"compx:{vid:X4}:{pid:X4}";
                var name = ResolveFriendly24GName(vid, pid, info.ProductName, WirelessDeviceKind.Mouse);
                var read = QueryCompx17Battery(info);
                seenGroups.Add(groupKey);
                if (read.Percent.HasValue)
                {
                    var st = new WirelessDeviceStatus(devKey, name, WirelessDeviceKind.Mouse, "2.4G", read.Percent.Value, read.Charging, false);
                    _lastKnown[devKey] = st;
                    results.Add(st);
                }
                else if (_lastKnown.TryGetValue(devKey, out var prev))
                {
                    results.Add(prev with { Asleep = true, Charging = false });
                }
                else
                {
                    results.Add(new WirelessDeviceStatus(devKey, name, WirelessDeviceKind.Mouse, "2.4G", null, false, Asleep: true));
                }
                continue;
            }

            // 2. AULA / 3554:FA09 20-byte 2.4G Keyboard protocol
            if (vid == 0x3554 && pid == 0xFA09 && info.UsagePage == 0xFF02 && info.Usage == 0x0002 && info.InputLength >= 20 && info.OutputLength >= 20)
            {
                var devKey = $"aula:{vid:X4}:{pid:X4}";
                var name = string.IsNullOrWhiteSpace(info.ProductName) ? "2.4G 无线键盘" : info.ProductName;
                var pct = QueryAula20Battery(info);
                var st = MergeWithLastKnown(devKey, name, WirelessDeviceKind.Keyboard, "2.4G", pct, false);
                results.Add(st);
                seenGroups.Add(groupKey);
                continue;
            }

            // 3. VGN / ATK 2.4G Keyboard (0xFF1C, 64-byte report 0x04 cmd 0x1A)
            if ((vid == 0x320F || vid == 0x373B) && UsagePageMatches(info.UsagePage, 0xFF1C) && info.InputLength >= 64 && info.OutputLength >= 64)
            {
                var devKey = $"vgnkb:{vid:X4}:{pid:X4}";
                var name = string.IsNullOrWhiteSpace(info.ProductName) ? "VGN/ATK 无线键盘" : info.ProductName;
                var (pct, chg) = QueryVgnKeyboardBattery(info);
                var st = MergeWithLastKnown(devKey, name, WirelessDeviceKind.Keyboard, "2.4G", pct, chg);
                results.Add(st);
                seenGroups.Add(groupKey);
                continue;
            }

            // 3b. RK R98Pro V2 2.4G Receiver (258A:01BB / 258A:0233, 64-byte Output/Input report 0x0A, cmd 0x4A)
            if (vid == 0x258A && pid is 0x01BB or 0x0233 && UsagePageMatches(info.UsagePage, 0xFF00) && info.InputLength >= 64 && info.OutputLength >= 64 && !seenGroups.Contains(groupKey))
            {
                var devKey = $"rk:{vid:X4}:{pid:X4}";
                var pct = QueryRkSinowealthBattery(info);
                var st = MergeWithLastKnown(devKey, "RK 无线键盘 (2.4G)", WirelessDeviceKind.Keyboard, "2.4G", pct, false);
                results.Add(st);
                seenGroups.Add(groupKey);
                continue;
            }

            // 4. Logitech HID++ 2.0 / 1.0 Receiver & Lightspeed (0x046D)
            if (vid == 0x046D && UsagePageMatches(info.UsagePage, 0xFF00) && info.InputLength == 20 && info.OutputLength == 20)
            {
                var devKey = $"logi:{vid:X4}:{pid:X4}";
                if (!seenGroups.Contains(groupKey))
                {
                    var (pct, chg, logiName) = QueryLogitechHidppBattery(info, pid);
                    var finalName = !string.IsNullOrWhiteSpace(logiName) ? logiName : (string.IsNullOrWhiteSpace(info.ProductName) ? "Logitech 无线设备" : info.ProductName);
                    var st = MergeWithLastKnown(devKey, finalName, WirelessDeviceKind.Mouse, "2.4G", pct, chg);
                    results.Add(st);
                    seenGroups.Add(groupKey);
                }
                continue;
            }

            // 5. Standard HID Feature Battery Usages (0x06/0x20, 0x85/0x65, 0x85/0x66, 0x0D/0x3B)
            if (info.FeatureBatteries.Count > 0 && !seenGroups.Contains(groupKey))
            {
                var pct = QueryStandardHidFeatureBattery(info);
                if (pct.HasValue)
                {
                    var devKey = $"hidbat:{vid:X4}:{pid:X4}";
                    var kind = GuessKindFromName(info.ProductName, info.UsagePage, info.Usage);
                    var name = ResolveFriendly24GName(vid, pid, info.ProductName, kind);
                    var st = new WirelessDeviceStatus(devKey, name, kind, "2.4G", pct.Value, false);
                    _lastKnown[devKey] = st;
                    results.Add(st);
                    seenGroups.Add(groupKey);
                    continue;
                }
            }

            // Track known 2.4G wireless dongles (e.g. 25A7:FA70 Royal Kludge RK 2.4G Receiver)
            if (!seenGroups.Contains(groupKey) && IsLikelyWirelessReceiver(vid, pid, info.ProductName))
            {
                var kind = (vid, pid) is (0x25A7, 0xFA70)
                    ? WirelessDeviceKind.Keyboard
                    : GuessKindFromName(info.ProductName, info.UsagePage, info.Usage);
                if (!receiverFallbacks.TryGetValue(groupKey, out var existing) ||
                    (existing.Kind != WirelessDeviceKind.Keyboard && kind == WirelessDeviceKind.Keyboard) ||
                    (existing.Kind == WirelessDeviceKind.Receiver && kind != WirelessDeviceKind.Receiver))
                {
                    receiverFallbacks[groupKey] = (vid, pid, ResolveFriendly24GName(vid, pid, info.ProductName, kind), kind);
                }
            }
        }

        // Add any attached 2.4G receiver that didn't expose a vendor battery frame so user sees all connected wireless receivers clearly
        foreach (var (grp, rec) in receiverFallbacks)
        {
            if (seenGroups.Contains(grp)) continue;
            results.Add(new WirelessDeviceStatus($"rec:{rec.Vid:X4}:{rec.Pid:X4}", rec.Name, rec.Kind, "2.4G", null, false, false));
        }
    }

    private WirelessDeviceStatus MergeWithLastKnown(string key, string name, WirelessDeviceKind kind, string conn, int? pct, bool charging)
    {
        if (pct.HasValue)
        {
            var fresh = new WirelessDeviceStatus(key, name, kind, conn, pct.Value, charging, false);
            _lastKnown[key] = fresh;
            return fresh;
        }
        if (_lastKnown.TryGetValue(key, out var prev))
            return prev with { Asleep = true, Charging = false };
        return new WirelessDeviceStatus(key, name, kind, conn, null, false, Asleep: true);
    }

    private static (int? Percent, bool Charging) QueryCompx17Battery(HidCollectionInfo info)
    {
        try
        {
            using var dev = new OverlappedHidHandle(info.Path, info.InputLength, info.OutputLength, info.FeatureLength, readWrite: true);
            dev.Flush();

            var req = new byte[17];
            req[0] = 0x08;
            req[1] = 0x04; // Battery command
            int sum = 0;
            for (int i = 0; i < 16; i++) sum += req[i];
            req[16] = (byte)(0x55 - sum);

            dev.WriteOutput(req, timeoutMs: 300);

            long deadline = Environment.TickCount64 + 400;
            while (Environment.TickCount64 < deadline)
            {
                int left = (int)Math.Max(20, deadline - Environment.TickCount64);
                var reply = dev.ReadInput(left);
                if (reply is null || reply.Length < 17) continue;
                if (reply[0] != 0x08 || reply[1] != 0x04) continue;

                int rsum = 0;
                for (int i = 0; i < 17; i++) rsum += reply[i];
                if ((byte)rsum != 0x55) continue;

                bool allZero = true;
                for (int i = 2; i < 16; i++) allZero &= reply[i] == 0;
                if (allZero) continue;

                int pct = reply[6];
                bool charging = reply[7] == 1;
                if (pct is > 0 and <= 100)
                    return (pct, charging);
                if (pct == 0)
                    return (null, false); // Mouse asleep
            }
        }
        catch { }
        return (null, false);
    }

    private static int? QueryAula20Battery(HidCollectionInfo info)
    {
        try
        {
            using var dev = new OverlappedHidHandle(info.Path, info.InputLength, info.OutputLength, info.FeatureLength, readWrite: true);
            dev.Flush();
            var req = new byte[20];
            req[0] = 0x13; req[1] = 0x05; req[2] = 0x01;
            int sum = 0;
            for (int i = 0; i < 19; i++) sum += req[i];
            req[19] = (byte)sum;
            dev.WriteOutput(req, 300);

            long deadline = Environment.TickCount64 + 400;
            while (Environment.TickCount64 < deadline)
            {
                var rep = dev.ReadInput((int)Math.Max(20, deadline - Environment.TickCount64));
                if (rep is not null && rep.Length >= 7 && rep[0] == 0x13 && rep[1] == 0x0A && rep[5] == 0x05 && rep[6] is > 0 and <= 100)
                    return rep[6];
            }
        }
        catch { }
        return null;
    }

    private static (int? Percent, bool Charging) QueryVgnKeyboardBattery(HidCollectionInfo info)
    {
        try
        {
            using var dev = new OverlappedHidHandle(info.Path, info.InputLength, info.OutputLength, info.FeatureLength, readWrite: true);
            dev.Flush();
            var req = new byte[64];
            req[0] = 0x04; req[3] = 0x1A; req[4] = 0x06;
            dev.WriteOutput(req, 300);

            long deadline = Environment.TickCount64 + 400;
            while (Environment.TickCount64 < deadline)
            {
                var rep = dev.ReadInput((int)Math.Max(20, deadline - Environment.TickCount64));
                if (rep is not null && rep.Length >= 10 && rep[0] == 0x04 && rep[3] == 0x1A && rep[8] is > 0 and <= 100)
                    return (rep[8], rep[9] != 0);
            }
        }
        catch { }
        return (null, false);
    }

    private static int? QueryRkSinowealthBattery(HidCollectionInfo info)
    {
        try
        {
            using var dev = new OverlappedHidHandle(info.Path, info.InputLength, info.OutputLength, info.FeatureLength, readWrite: true);
            dev.Flush();
            var req = new byte[64];
            req[0] = 0x0A; req[1] = 0x4A; // Battery command
            dev.WriteOutput(req, 300);

            long deadline = Environment.TickCount64 + 500;
            while (Environment.TickCount64 < deadline)
            {
                var rep = dev.ReadInput((int)Math.Max(20, deadline - Environment.TickCount64));
                if (rep is not null && rep.Length >= 7 && rep[0] == 0x0A && rep[1] == 0x4A && rep[5] is > 0 and <= 100)
                    return rep[5];
            }
        }
        catch { }
        return null;
    }

    private static (int? Percent, bool Charging, string? Name) QueryLogitechHidppBattery(HidCollectionInfo info, int pid)
    {
        try
        {
            using var dev = new OverlappedHidHandle(info.Path, 20, 20, info.FeatureLength, readWrite: true);
            byte[] slots = pid is >= 0xC500 and <= 0xC5FF ? [1, 2] : [0xFF, 1];
            foreach (var slot in slots)
            {
                // Query UnifiedBattery (0x1004) or BatteryStatus (0x1000) via Root (0x00) function 0
                foreach (ushort feat in new ushort[] { 0x1004, 0x1000 })
                {
                    dev.Flush();
                    var rootReq = new byte[20];
                    rootReq[0] = 0x11; rootReq[1] = slot; rootReq[2] = 0x00; rootReq[3] = 0x0E;
                    rootReq[4] = (byte)(feat >> 8); rootReq[5] = (byte)feat;
                    dev.WriteOutput(rootReq, 250);

                    var rootRep = WaitHidppReply(dev, slot, 0x00, 0x0E, 300);
                    if (rootRep is null || rootRep[4] == 0) continue;

                    byte featIdx = rootRep[4];
                    byte fn = feat == 0x1004 ? (byte)0x1E : (byte)0x0E; // function 1 for 0x1004, function 0 for 0x1000
                    var batReq = new byte[20];
                    batReq[0] = 0x11; batReq[1] = slot; batReq[2] = featIdx; batReq[3] = fn;
                    dev.WriteOutput(batReq, 250);

                    var batRep = WaitHidppReply(dev, slot, featIdx, fn, 300);
                    if (batRep is not null && batRep[4] is > 0 and <= 100)
                    {
                        bool chg = feat == 0x1004 ? batRep[6] is >= 1 and <= 3 : batRep[6] is >= 1 and <= 4;
                        return (batRep[4], chg, null);
                    }
                }
            }
        }
        catch { }
        return (null, false, null);
    }

    private static byte[]? WaitHidppReply(OverlappedHidHandle dev, byte slot, byte featIdx, byte fnSw, int timeoutMs)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            var rep = dev.ReadInput((int)Math.Max(20, deadline - Environment.TickCount64));
            if (rep is null || rep.Length < 7) continue;
            if (rep[0] is not (0x10 or 0x11) || rep[1] != slot) continue;
            if (rep[2] == featIdx && rep[3] == fnSw) return rep;
            if (rep[2] is 0xFF or 0x8F) return null;
        }
        return null;
    }

    private static int? QueryStandardHidFeatureBattery(HidCollectionInfo info)
    {
        try
        {
            using var dev = new OverlappedHidHandle(info.Path, info.InputLength, info.OutputLength, Math.Max(info.FeatureLength, 8), readWrite: false);
            foreach (var fb in info.FeatureBatteries)
            {
                var report = dev.GetFeature(fb.ReportId, Math.Max(info.FeatureLength, 4), 250);
                if (report is null) continue;
                if (!HidD_GetPreparsedData(dev.Handle, out var preparsed)) continue;
                try
                {
                    if (HidP_GetUsageValue(2, fb.Page, fb.Link, fb.Usage, out uint val, preparsed, report, (uint)report.Length) == HIDP_STATUS_SUCCESS)
                    {
                        int min = fb.Min, max = fb.Max <= fb.Min ? fb.Min + 100 : fb.Max;
                        if (val >= min && val <= max)
                        {
                            int pct = (int)Math.Round(((int)val - min) * 100.0 / (max - min));
                            if (pct is > 0 and <= 100) return pct;
                        }
                    }
                }
                finally
                {
                    HidD_FreePreparsedData(preparsed);
                }
            }
        }
        catch { }
        return null;
    }

    private static async Task ReadBluetoothDevicesAsync(List<WirelessDeviceStatus> results, CancellationToken ct)
    {
        try
        {
            string[] props = [AepConnected, AepAddress, AepCategory];
            var classicTask = DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true), props, DeviceInformationKind.AssociationEndpoint).AsTask(ct);
            var bleTask = DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true), props, DeviceInformationKind.AssociationEndpoint).AsTask(ct);

            await Task.WhenAll(classicTask, bleTask).ConfigureAwait(false);
            var nodeLevels = ReadBluetoothPnPBatteryLevels();

            var seenAddr = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var allEndpoints = (await classicTask.ConfigureAwait(false)).Select(e => (Info: e, IsBle: false))
                .Concat((await bleTask.ConfigureAwait(false)).Select(e => (Info: e, IsBle: true)));

            foreach (var (ep, isBle) in allEndpoints)
            {
                if (!ep.Properties.TryGetValue(AepConnected, out var connObj) || connObj is not true)
                    continue;

                var rawAddr = ep.Properties.TryGetValue(AepAddress, out var aObj) ? aObj?.ToString() : null;
                var addr = ExtractHex12Address(rawAddr) ?? ExtractHex12Address(ep.Id);
                if (addr is null || !seenAddr.Add(addr))
                    continue;

                int? level = nodeLevels.TryGetValue(addr, out var cachedLv) ? cachedLv : null;

                // Try BLE GATT Battery Service (0x180F) if Windows PnP cache didn't have it
                if (level is null && isBle)
                {
                    try
                    {
                        using var bleDev = await BluetoothLEDevice.FromIdAsync(ep.Id).AsTask(ct).ConfigureAwait(false);
                        if (bleDev is not null)
                        {
                            var svcs = await bleDev.GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Cached).AsTask(ct).ConfigureAwait(false);
                            if (svcs.Status != GattCommunicationStatus.Success || svcs.Services.Count == 0)
                                svcs = await bleDev.GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Uncached).AsTask(ct).ConfigureAwait(false);

                            if (svcs.Status == GattCommunicationStatus.Success && svcs.Services.Count > 0)
                            {
                                using var svc = svcs.Services[0];
                                var chars = await svc.GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel, BluetoothCacheMode.Cached).AsTask(ct).ConfigureAwait(false);
                                if (chars.Status != GattCommunicationStatus.Success || chars.Characteristics.Count == 0)
                                    chars = await svc.GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel, BluetoothCacheMode.Uncached).AsTask(ct).ConfigureAwait(false);

                                if (chars.Status == GattCommunicationStatus.Success && chars.Characteristics.Count > 0)
                                {
                                    var readRes = await chars.Characteristics[0].ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(ct).ConfigureAwait(false);
                                    if (readRes.Status != GattCommunicationStatus.Success)
                                        readRes = await chars.Characteristics[0].ReadValueAsync(BluetoothCacheMode.Cached).AsTask(ct).ConfigureAwait(false);

                                    if (readRes.Status == GattCommunicationStatus.Success && readRes.Value is { Length: > 0 } buf)
                                    {
                                        var bytes = buf.ToArray();
                                        if (bytes[0] <= 100) level = bytes[0];
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }

                var cat = ep.Properties.TryGetValue(AepCategory, out var cObj) ? cObj?.ToString() ?? "" : "";
                var kind = GuessBluetoothKind(ep.Name, cat);
                results.Add(new WirelessDeviceStatus($"bt:{addr}", string.IsNullOrWhiteSpace(ep.Name) ? "蓝牙设备" : ep.Name, kind, "蓝牙", level));
            }
        }
        catch { }
    }

    private static Dictionary<string, int> ReadBluetoothPnPBatteryLevels()
    {
        // Fast SetupAPI scan over present BTH / BTHLE device nodes (<2ms, zero WinRT AQS full-tree hang!)
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var emptyGuid = Guid.Empty;
        IntPtr set = SetupDiGetClassDevs(ref emptyGuid, IntPtr.Zero, IntPtr.Zero, 0x02 | 0x04); // DIGCF_PRESENT | DIGCF_ALLCLASSES
        if (set == new IntPtr(-1)) return map;
        try
        {
            var devData = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
            var propKey = new DEVPROPKEY { fmtid = new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"), pid = 2 };
            var idBuf = new char[256];
            for (int i = 0; SetupDiEnumDeviceInfo(set, i, ref devData); i++)
            {
                if (!SetupDiGetDeviceInstanceIdW(set, ref devData, idBuf, idBuf.Length, out _))
                    continue;
                int nul = Array.IndexOf(idBuf, '\0');
                var instId = new string(idBuf, 0, nul >= 0 ? nul : idBuf.Length);
                if (!instId.StartsWith("BTH", StringComparison.OrdinalIgnoreCase))
                    continue;
                var addr = ExtractHex12Address(instId);
                if (addr is null) continue;
                if (SetupDiGetDevicePropertyW(set, ref devData, ref propKey, out uint propType, out byte levelByte, 1, out _, 0) &&
                    propType == 3 && levelByte <= 100) // DEVPROP_TYPE_BYTE = 3
                {
                    map[addr] = levelByte;
                }
            }
        }
        catch { }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return map;
    }

    private static void ReadXInputControllers(List<WirelessDeviceStatus> results)
    {
        try
        {
            for (uint i = 0; i < 4; i++)
            {
                var info = new XINPUT_BATTERY_INFORMATION();
                // devType 0 = GAMEPAD
                if (XInputGetBatteryInformation(i, 0, ref info) == 0)
                {
                    // BatteryType: 0=Disconnected, 1=Wired, 2=Alkaline, 3=NiMH
                    if (info.BatteryType is 2 or 3)
                    {
                        int pct = info.BatteryLevel switch
                        {
                            0 => 10, // Empty
                            1 => 30, // Low
                            2 => 65, // Medium
                            _ => 100 // Full
                        };
                        results.Add(new WirelessDeviceStatus($"xinput:{i}", $"Xbox 无线手柄 #{i + 1}", WirelessDeviceKind.Gamepad, "XInput", pct));
                    }
                }
            }
        }
        catch { }
    }

    private static string ResolveFriendly24GName(int vid, int pid, string rawName, WirelessDeviceKind kind)
    {
        if (!string.IsNullOrWhiteSpace(rawName) &&
            !rawName.Equals("USB Input Device", StringComparison.OrdinalIgnoreCase) &&
            !rawName.Equals("2.4G Wireless Receiver", StringComparison.OrdinalIgnoreCase))
        {
            return rawName.Replace("1K Dongle", "无线鼠标 (1K)", StringComparison.OrdinalIgnoreCase)
                          .Replace("4K Dongle", "无线鼠标 (4K)", StringComparison.OrdinalIgnoreCase)
                          .Replace("8K Dongle", "无线鼠标 (8K)", StringComparison.OrdinalIgnoreCase);
        }

        return (vid, pid) switch
        {
            (0x3554, 0xF58E) => "VXE 无线鼠标 (1K)",
            (0x25A7, 0xFA70) => "RK 无线键盘 (2.4G)",
            _ => kind switch
            {
                WirelessDeviceKind.Mouse => "2.4G 无线鼠标",
                WirelessDeviceKind.Keyboard => "2.4G 无线键盘",
                WirelessDeviceKind.Headset => "2.4G 无线耳机",
                _ => string.IsNullOrWhiteSpace(rawName) ? "2.4G 无线接收器" : rawName
            }
        };
    }

    private static bool IsLikelyWirelessReceiver(int vid, int pid, string name)
    {
        if ((vid, pid) is (0x25A7, 0xFA70) or (0x3554, 0xF58E) or (0x046D, >= 0xC500 and <= 0xC5FF))
            return true;
        if (string.IsNullOrWhiteSpace(name)) return false;
        return name.Contains("Wireless", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Dongle", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Receiver", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("2.4G", StringComparison.OrdinalIgnoreCase);
    }

    private static WirelessDeviceKind GuessKindFromName(string name, int usagePage, int usage)
    {
        if (name.Contains("Mouse", StringComparison.OrdinalIgnoreCase)) return WirelessDeviceKind.Mouse;
        if (name.Contains("Keyboard", StringComparison.OrdinalIgnoreCase)) return WirelessDeviceKind.Keyboard;
        if (name.Contains("Headset", StringComparison.OrdinalIgnoreCase) || name.Contains("Audio", StringComparison.OrdinalIgnoreCase))
            return WirelessDeviceKind.Headset;
        if (usagePage == 0x01 && usage == 0x02) return WirelessDeviceKind.Mouse;
        if (usagePage == 0x01 && usage == 0x06) return WirelessDeviceKind.Keyboard;
        if (usagePage == 0x01 && usage is 0x04 or 0x05) return WirelessDeviceKind.Gamepad;
        return WirelessDeviceKind.Receiver;
    }

    private static WirelessDeviceKind GuessBluetoothKind(string name, string category)
    {
        var combined = $"{name} {category}";
        if (combined.Contains("Mouse", StringComparison.OrdinalIgnoreCase) || combined.Contains("鼠标"))
            return WirelessDeviceKind.Mouse;
        if (combined.Contains("Keyboard", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("键盘") ||
            Regex.IsMatch(name, @"\b(RK\d|RK\s|Royal\s*Kludge|AULA|Keychron|HHKB|Filco|Leopold|Rainy|VGN|K75|K87|K98|F75|F87|F99)", RegexOptions.IgnoreCase))
            return WirelessDeviceKind.Keyboard;
        if (combined.Contains("Game", StringComparison.OrdinalIgnoreCase) || combined.Contains("Controller", StringComparison.OrdinalIgnoreCase) || combined.Contains("Xbox", StringComparison.OrdinalIgnoreCase))
            return WirelessDeviceKind.Gamepad;
        return WirelessDeviceKind.Headset;
    }

    private static string? ExtractHex12Address(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        // Strip {GUID} blocks first so Bluetooth SIG base UUID (00805F9B34FB) is never mistaken for the device MAC!
        var noGuids = Regex.Replace(text, @"\{[0-9A-Fa-f\-]{36}\}", "");
        var stripped = noGuids.Replace(":", "").Replace("-", "");
        var matches = Regex.Matches(stripped, "(?<![0-9A-Fa-f])([0-9A-Fa-f]{12})(?![0-9A-Fa-f])");
        foreach (Match m in matches)
        {
            var val = m.Groups[1].Value.ToUpperInvariant();
            if (val is not ("000000000000" or "00805F9B34FB"))
                return val;
        }
        return null;
    }

    private static bool UsagePageMatches(int reported, int expected)
        => reported == expected || reported == (((expected & 0xFF) << 8) | ((expected >> 8) & 0xFF));

    private static bool IsBluetoothHid(string path)
        => path.Contains("{00001124-", StringComparison.OrdinalIgnoreCase) ||
           path.Contains("{00001812-", StringComparison.OrdinalIgnoreCase) ||
           path.Contains("bthledevice", StringComparison.OrdinalIgnoreCase);

    private static (int Vid, int Pid) ParseVidPid(string path)
    {
        var m = Regex.Match(path, @"vid[_&][0-9a-f]*?([0-9a-f]{4})[_&]pid[_&]([0-9a-f]{4})", RegexOptions.IgnoreCase);
        return m.Success ? (Convert.ToInt32(m.Groups[1].Value, 16), Convert.ToInt32(m.Groups[2].Value, 16)) : (0, 0);
    }

    private static string GroupKey(string path, int vid, int pid) => $"{vid:X4}:{pid:X4}";

    public void Dispose()
    {
        _disposed = true;
        _scanLock.Dispose();
    }

    // ---- Native Win32 HID & XInput Interop ----

    private sealed class HidCollectionInfo
    {
        public required string Path;
        public int Vendor, Product, UsagePage, Usage, InputLength, OutputLength, FeatureLength;
        public string ProductName = "";
        public List<(byte ReportId, ushort Page, ushort Usage, ushort Link, int Min, int Max)> FeatureBatteries = [];
    }

    private static List<string> EnumerateHidPaths()
    {
        var paths = new List<string>();
        HidD_GetHidGuid(out var guid);
        IntPtr set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, 0x02 | 0x10);
        if (set == new IntPtr(-1)) return paths;
        try
        {
            for (int i = 0; ; i++)
            {
                var data = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data)) break;
                SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out int need, IntPtr.Zero);
                if (need <= 0) continue;
                IntPtr buf = Marshal.AllocHGlobal(need + 4);
                try
                {
                    Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                    if (SetupDiGetDeviceInterfaceDetail(set, ref data, buf, need, out _, IntPtr.Zero))
                        paths.Add(Marshal.PtrToStringUni(buf + 4) ?? "");
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return paths;
    }

    private static HidCollectionInfo? QueryHidCollection(string path, bool readProductString = false)
    {
        IntPtr handle = CreateFile(path, 0, 1 | 2, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle == new IntPtr(-1)) return null;
        try
        {
            var attr = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
            if (!HidD_GetAttributes(handle, ref attr)) return null;
            var info = new HidCollectionInfo { Path = path, Vendor = attr.VendorID, Product = attr.ProductID };
            if (HidD_GetPreparsedData(handle, out var preparsed))
            {
                try
                {
                    var caps = new ushort[32];
                    HidP_GetCaps(preparsed, caps);
                    info.Usage = caps[0]; info.UsagePage = caps[1];
                    info.InputLength = caps[2]; info.OutputLength = caps[3]; info.FeatureLength = caps[4];
                    int featValCount = caps[30];
                    if (featValCount > 0)
                    {
                        var vbuf = new byte[72 * featValCount];
                        ushort vlen = (ushort)featValCount;
                        if (HidP_GetValueCaps(2, vbuf, ref vlen, preparsed) == HIDP_STATUS_SUCCESS)
                        {
                            for (int i = 0; i < vlen; i++)
                            {
                                int at = i * 72;
                                ushort page = BitConverter.ToUInt16(vbuf, at);
                                ushort usage = BitConverter.ToUInt16(vbuf, at + 56);
                                if ((page, usage) is (0x06, 0x20) or (0x0D, 0x3B) or (0x85, 0x65) or (0x85, 0x66))
                                {
                                    info.FeatureBatteries.Add((
                                        vbuf[at + 2], page, usage,
                                        BitConverter.ToUInt16(vbuf, at + 6),
                                        BitConverter.ToInt32(vbuf, at + 40),
                                        BitConverter.ToInt32(vbuf, at + 44)));
                                }
                            }
                        }
                    }
                }
                finally { HidD_FreePreparsedData(preparsed); }
            }

            if (readProductString)
            {
                var sbuf = new byte[256];
                if (HidD_GetProductString(handle, sbuf, sbuf.Length))
                {
                    var str = Encoding.Unicode.GetString(sbuf);
                    int nul = str.IndexOf('\0');
                    info.ProductName = (nul >= 0 ? str[..nul] : str).Trim();
                }
            }
            return info;
        }
        finally { CloseHandle(handle); }
    }

    private sealed class OverlappedHidHandle : IDisposable
    {
        private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, FILE_FLAG_OVERLAPPED = 0x40000000;
        private const int ERROR_IO_PENDING = 997;
        private const uint IOCTL_HID_GET_FEATURE = 0xB0192;

        public IntPtr Handle { get; private set; }
        private readonly int _inLen, _outLen;

        public OverlappedHidHandle(string path, int inLen, int outLen, int featLen, bool readWrite)
        {
            Handle = CreateFile(path, readWrite ? (GENERIC_READ | GENERIC_WRITE) : 0, 1 | 2, IntPtr.Zero, 3, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (Handle == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            _inLen = inLen > 0 ? inLen : 64;
            _outLen = outLen > 0 ? outLen : 64;
        }

        public void Flush() => HidD_FlushQueue(Handle);

        public void WriteOutput(byte[] data, int timeoutMs)
        {
            var buf = new byte[Math.Max(_outLen, data.Length)];
            Array.Copy(data, buf, data.Length);
            IntPtr ov = AllocOverlapped(), mem = Marshal.AllocHGlobal(buf.Length);
            try
            {
                Marshal.Copy(buf, 0, mem, buf.Length);
                if (!WriteFile(Handle, mem, buf.Length, IntPtr.Zero, ov))
                {
                    if (Marshal.GetLastWin32Error() != ERROR_IO_PENDING) throw new IOException();
                    if (WaitForSingleObject(GetOvEvent(ov), (uint)timeoutMs) != 0)
                    {
                        CancelIoEx(Handle, ov);
                        if (WaitForSingleObject(GetOvEvent(ov), 500) != 0) { mem = IntPtr.Zero; ov = IntPtr.Zero; }
                        throw new TimeoutException();
                    }
                }
                GetOverlappedResult(Handle, ov, out _, true);
            }
            finally
            {
                if (mem != IntPtr.Zero) Marshal.FreeHGlobal(mem);
                if (ov != IntPtr.Zero) FreeOverlapped(ov);
            }
        }

        public byte[]? ReadInput(int timeoutMs)
        {
            IntPtr ov = AllocOverlapped(), mem = Marshal.AllocHGlobal(_inLen);
            try
            {
                if (!ReadFile(Handle, mem, _inLen, IntPtr.Zero, ov))
                {
                    if (Marshal.GetLastWin32Error() != ERROR_IO_PENDING) return null;
                    if (WaitForSingleObject(GetOvEvent(ov), (uint)timeoutMs) != 0)
                    {
                        CancelIoEx(Handle, ov);
                        if (WaitForSingleObject(GetOvEvent(ov), 500) != 0) { mem = IntPtr.Zero; ov = IntPtr.Zero; }
                        return null;
                    }
                }
                if (!GetOverlappedResult(Handle, ov, out int done, true) || done <= 0) return null;
                var outBytes = new byte[done];
                Marshal.Copy(mem, outBytes, 0, done);
                return outBytes;
            }
            finally
            {
                if (mem != IntPtr.Zero) Marshal.FreeHGlobal(mem);
                if (ov != IntPtr.Zero) FreeOverlapped(ov);
            }
        }

        public byte[]? GetFeature(byte reportId, int length, int timeoutMs)
        {
            var buf = new byte[Math.Max(length, 2)];
            buf[0] = reportId;
            IntPtr ov = AllocOverlapped(), mem = Marshal.AllocHGlobal(buf.Length);
            try
            {
                Marshal.Copy(buf, 0, mem, buf.Length);
                if (!DeviceIoControl(Handle, IOCTL_HID_GET_FEATURE, mem, buf.Length, mem, buf.Length, IntPtr.Zero, ov))
                {
                    if (Marshal.GetLastWin32Error() != ERROR_IO_PENDING) return null;
                    if (WaitForSingleObject(GetOvEvent(ov), (uint)timeoutMs) != 0)
                    {
                        CancelIoEx(Handle, ov);
                        if (WaitForSingleObject(GetOvEvent(ov), 500) != 0) { mem = IntPtr.Zero; ov = IntPtr.Zero; }
                        return null;
                    }
                }
                if (!GetOverlappedResult(Handle, ov, out _, true)) return null;
                Marshal.Copy(mem, buf, 0, buf.Length);
                return buf;
            }
            finally
            {
                if (mem != IntPtr.Zero) Marshal.FreeHGlobal(mem);
                if (ov != IntPtr.Zero) FreeOverlapped(ov);
            }
        }

        private static IntPtr AllocOverlapped()
        {
            int sz = 2 * IntPtr.Size + 8 + IntPtr.Size;
            IntPtr ov = Marshal.AllocHGlobal(sz);
            for (int i = 0; i < sz; i++) Marshal.WriteByte(ov, i, 0);
            Marshal.WriteIntPtr(ov, 2 * IntPtr.Size + 8, CreateEvent(IntPtr.Zero, true, false, null));
            return ov;
        }
        private static IntPtr GetOvEvent(IntPtr ov) => Marshal.ReadIntPtr(ov, 2 * IntPtr.Size + 8);
        private static void FreeOverlapped(IntPtr ov) { CloseHandle(GetOvEvent(ov)); Marshal.FreeHGlobal(ov); }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero && Handle != new IntPtr(-1))
            {
                CloseHandle(Handle);
                Handle = IntPtr.Zero;
            }
        }
    }

    private const int HIDP_STATUS_SUCCESS = 0x00110000;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY { public Guid fmtid; public uint pid; }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID; public ushort ProductID; public ushort VersionNumber; }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_BATTERY_INFORMATION { public byte BatteryType; public byte BatteryLevel; }

    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetAttributes(IntPtr handle, ref HIDD_ATTRIBUTES attr);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetPreparsedData(IntPtr handle, out IntPtr data);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr data, [Out] ushort[] caps);
    [DllImport("hid.dll")] private static extern int HidP_GetValueCaps(int type, [Out] byte[] caps, ref ushort len, IntPtr data);
    [DllImport("hid.dll")] private static extern int HidP_GetUsageValue(int type, ushort page, ushort link, ushort usage, out uint val, IntPtr data, byte[] report, uint len);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_FlushQueue(IntPtr handle);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetProductString(IntPtr handle, byte[] buffer, int len);

    [DllImport("setupapi.dll", SetLastError = true)] private static extern IntPtr SetupDiGetClassDevs(ref Guid cls, IntPtr en, IntPtr win, int flags);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInfo(IntPtr set, int idx, ref SP_DEVINFO_DATA data);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref SP_DEVINFO_DATA data, [Out] char[] id, int size, out int req);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref SP_DEVINFO_DATA data, ref DEVPROPKEY key, out uint type, out byte buf, int size, out int req, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid cls, int idx, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SetupDiGetDeviceInterfaceDetailW")]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int need, IntPtr info);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    private static extern IntPtr CreateFile(string path, uint access, uint share, IntPtr sec, uint creation, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateEvent(IntPtr sec, bool manual, bool init, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadFile(IntPtr h, IntPtr buf, int len, IntPtr read, IntPtr ov);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteFile(IntPtr h, IntPtr buf, int len, IntPtr written, IntPtr ov);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetOverlappedResult(IntPtr h, IntPtr ov, out int done, bool wait);
    [DllImport("kernel32.dll")] private static extern bool CancelIoEx(IntPtr h, IntPtr ov);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DeviceIoControl(IntPtr h, uint code, IntPtr inB, int inL, IntPtr outB, int outL, IntPtr ret, IntPtr ov);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr h, uint ms);

    [DllImport("xinput1_4.dll", ExactSpelling = true)]
    private static extern uint XInputGetBatteryInformation(uint dwUserIndex, byte devType, ref XINPUT_BATTERY_INFORMATION pBatteryInformation);
}
