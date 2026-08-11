using Microsoft.Win32;

namespace BrowserSync.Host.Startup;

/// <summary>Toggles a per-user (HKCU) Run-key entry so the host starts on login — no admin
/// elevation required.</summary>
public static class RunKeyManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "BrowserSync";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is not null;
    }

    public static void Enable()
    {
        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not determine the current process path.");
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        key.SetValue(ValueName, $"\"{exePath}\"", RegistryValueKind.String);
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
