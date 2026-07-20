using Microsoft.Win32;

namespace ReplayPad.UI;

/// <summary>Per-user "start with Windows" toggle (HKCU Run key).</summary>
internal static class StartupRegistry
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "ReplayPad";

    private const string LegacyRunValueName = "AudioReplayBuffer";

    /// <summary>Rewrites the pre-rename autostart entry to the new exe. Call once at startup.</summary>
    public static void MigrateLegacyEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key.GetValue(LegacyRunValueName) == null)
                return;
            key.DeleteValue(LegacyRunValueName, throwOnMissingValue: false);
            key.SetValue(RunValueName, $"\"{Environment.ProcessPath}\" --minimized");
        }
        catch
        {
            // Best-effort; the user can re-toggle autostart in Settings.
        }
    }

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(RunValueName) != null;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (enabled)
            key.SetValue(RunValueName, $"\"{Environment.ProcessPath}\" --minimized");
        else
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
    }
}
