using Microsoft.Win32;

namespace GoatDNS.App.Services;

/// <summary>
/// Start-with-Windows for the UI app via the per-user Run key. Per-user (HKCU) is deliberate:
/// the app runs unelevated, so it can write its own autostart without admin rights. The DNS
/// service has its own SCM autostart and is unaffected by this.
/// </summary>
internal static class StartupRegistry
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "GoatDNS";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is not null;
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            string? exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
                key.SetValue(ValueName, $"\"{exe}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
