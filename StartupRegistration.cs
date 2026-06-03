using Microsoft.Win32;

namespace Recorder;

public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LaptopOutputRecorder";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        var value = key?.GetValue(ValueName) as string;
        return string.Equals(value, StartupCommand, StringComparison.OrdinalIgnoreCase);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);

        if (enabled)
        {
            key.SetValue(ValueName, StartupCommand, RegistryValueKind.String);
            AppLog.Info($"Startup launch enabled: {StartupCommand}");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            AppLog.Info("Startup launch disabled.");
        }
    }

    private static string StartupCommand
    {
        get
        {
            var executablePath = Environment.ProcessPath
                ?? Application.ExecutablePath
                ?? throw new InvalidOperationException("Could not determine executable path.");

            return $"\"{executablePath}\"";
        }
    }
}
