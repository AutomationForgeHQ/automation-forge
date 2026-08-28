using Microsoft.Win32;

namespace Forge.Hub;

/// <summary>
/// "Start with Windows" is one value under HKCU\…\Run, pointing at this
/// executable with --tray. The registry is the truth, not a setting: what the
/// hub shows is what Windows will do, and an entry pointing at an old copy of
/// the hub reads as off.
/// </summary>
public static class Autostart
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "AutomationForge";

    public static bool Supported => OperatingSystem.IsWindows() && Environment.ProcessPath is not null;

    public static bool Enabled
    {
        get
        {
            if (!Supported) return false;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(Key);
                return key?.GetValue(Name) is string value
                       && value.Contains(Environment.ProcessPath!, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or IOException) { return false; }
        }
        set
        {
            if (!Supported) return;
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(Key);
                if (value) key.SetValue(Name, $"\"{Environment.ProcessPath}\" --tray");
                else key.DeleteValue(Name, throwOnMissingValue: false);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
            {
                // Nothing to do: the checkbox re-reads the registry and shows the truth.
            }
        }
    }
}
