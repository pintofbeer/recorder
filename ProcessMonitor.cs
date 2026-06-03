using System.Diagnostics;
using System.Text;

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

    public ForegroundWindowInfo? GetForegroundWindowInfo()
    {
        var foregroundWindow = NativeMethods.GetForegroundWindow();
        if (foregroundWindow == IntPtr.Zero)
        {
            return null;
        }

        NativeMethods.GetWindowThreadProcessId(foregroundWindow, out var processId);
        if (processId == 0)
        {
            return null;
        }

        string processName;
        try
        {
            processName = Process.GetProcessById((int)processId).ProcessName;
        }
        catch (ArgumentException)
        {
            return null;
        }

        var title = GetWindowTitle(foregroundWindow);
        return string.IsNullOrWhiteSpace(title)
            ? null
            : new ForegroundWindowInfo(processName, foregroundWindow, title);
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

    private static string GetWindowTitle(IntPtr windowHandle)
    {
        var length = NativeMethods.GetWindowTextLengthW(windowHandle);
        if (length <= 0)
        {
            return "";
        }

        var builder = new StringBuilder(length + 1);
        _ = NativeMethods.GetWindowTextW(windowHandle, builder, builder.Capacity);
        return builder.ToString().Trim();
    }
}

public sealed record FocusedWatchedApp(string DisplayName, int ProcessId, string ProcessName, IntPtr WindowHandle, bool CaptureMicrophone);

public sealed record RecordingTarget(string DisplayName, int ProcessId, string ProcessName, IntPtr WindowHandle, bool CaptureMicrophone);

public sealed record ForegroundWindowInfo(string ProcessName, IntPtr WindowHandle, string Title);
