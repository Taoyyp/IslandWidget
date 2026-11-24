using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace IslandWidget.Models;

public static class ProxyDetector
{
    private const string InternetSettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOptionW(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

    public static (bool Enabled, string Server) ReadRawSystemProxy()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey);
            if (key is null) return (false, "");
            var enabled = key.GetValue("ProxyEnable") is int en && en != 0;
            var server = key.GetValue("ProxyServer") as string ?? "";
            return (enabled, server);
        }
        catch
        {
            return (false, "");
        }
    }

    public static bool SetSystemProxy(bool enable, int port)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, writable: true);
            if (key is null) return false;
            key.SetValue("ProxyEnable", enable ? 1 : 0, RegistryValueKind.DWord);
            if (enable && port > 0)
            {
                var existing = key.GetValue("ProxyServer") as string ?? "";
                if (string.IsNullOrWhiteSpace(existing))
                    key.SetValue("ProxyServer", $"127.0.0.1:{port}", RegistryValueKind.String);
            }
            InternetSetOptionW(IntPtr.Zero, 39, IntPtr.Zero, 0); // INTERNET_OPTION_SETTINGS_CHANGED
            InternetSetOptionW(IntPtr.Zero, 37, IntPtr.Zero, 0); // INTERNET_OPTION_REFRESH
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string? DetectSystemProxy()
    {
        var (enabled, server) = ReadRawSystemProxy();
        return enabled ? Normalize(server) : null;
    }

    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        if (value.Contains(';'))
        {
            var entries = value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var https = entries.FirstOrDefault(e => e.StartsWith("https=", StringComparison.OrdinalIgnoreCase));
            var http = entries.FirstOrDefault(e => e.StartsWith("http=", StringComparison.OrdinalIgnoreCase));
            value = (https ?? http)?.Split('=', 2).ElementAtOrDefault(1) ?? entries[0];
        }
        if (value.StartsWith("socks", StringComparison.OrdinalIgnoreCase))
            return null;
        if (!value.Contains("://", StringComparison.Ordinal))
            value = "http://" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return null;
        if (uri.Scheme is not ("http" or "https"))
            return null;
        return uri.GetLeftPart(UriPartial.Authority);
    }
}
