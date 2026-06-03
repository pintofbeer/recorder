using System.Diagnostics;

namespace Recorder;

public sealed class ProcessMonitor
{
    private readonly Func<AppSettings> settingsProvider;

    public ProcessMonitor(Func<AppSettings> settingsProvider)
    {
        this.settingsProvider = settingsProvider;
    }

    public FocusedWatchedApp? GetFocusedWatchedApp()
    {
        var foregroundWindow = NativeMethods.GetForegroundWindow();
        if (foregroundWindow == IntPtr.Zero)
        {
            return null;
        }

        NativeMethods.GetWindowThreadProcessId(foregroundWindow, out var processId);
        if (processId == 0 || NativeMethods.IsIconic(foregroundWindow))
        {
            return null;
        }

        Process process;
        try
        {
            process = Process.GetProcessById((int)processId);
        }
        catch (ArgumentException)
        {
            return null;
        }

        var processName = process.ProcessName;
        var watchedApp = settingsProvider().WatchedApps.FirstOrDefault(app =>
            app.ProcessNames.Any(name => string.Equals(
                Path.GetFileNameWithoutExtension(name),
                processName,
                StringComparison.OrdinalIgnoreCase)));

        return watchedApp is null
            ? null
            : new FocusedWatchedApp(watchedApp.DisplayName, process.Id, processName, foregroundWindow, watchedApp.CaptureMicrophone);
    }

    public bool IsStillOpenAndVisible(RecordingTarget target)
    {
        try
        {
            var process = Process.GetProcessById(target.ProcessId);
            if (process.HasExited)
            {
                return false;
            }

            if (target.WindowHandle != IntPtr.Zero)
            {
                return NativeMethods.IsWindow(target.WindowHandle) && !NativeMethods.IsIconic(target.WindowHandle);
            }

            var windowHandle = process.MainWindowHandle;
            return windowHandle != IntPtr.Zero && !NativeMethods.IsIconic(windowHandle);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}

public sealed record FocusedWatchedApp(string DisplayName, int ProcessId, string ProcessName, IntPtr WindowHandle, bool CaptureMicrophone);

public sealed record RecordingTarget(string DisplayName, int ProcessId, string ProcessName, IntPtr WindowHandle, bool CaptureMicrophone);
