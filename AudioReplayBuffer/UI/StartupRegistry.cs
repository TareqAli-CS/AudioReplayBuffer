using Microsoft.Win32;

namespace AudioReplayBuffer.UI;

/// <summary>Per-user "start with Windows" toggle (HKCU Run key).</summary>
internal static class StartupRegistry
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "AudioReplayBuffer";

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
