using System.Diagnostics;
using Microsoft.Win32;

namespace IslandWidget.Services;

public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppValueName = "IslandWidget";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(AppValueName) is string s && !string.IsNullOrWhiteSpace(s);
        }
        catch
        {
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null) return;
            if (!enabled)
            {
                key.DeleteValue(AppValueName, throwOnMissingValue: false);
                return;
            }

            var exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(exe))
            {
                key.SetValue(AppValueName, $"\"{exe}\"", RegistryValueKind.String);
            }
        }
        catch
        {
            // Ignore registry write failures in restricted environments
        }
    }
}
