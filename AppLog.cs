namespace Recorder;

public static class AppLog
{
    private static readonly object Lock = new();

    public static string DirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LaptopOutputRecorder");

    public static string FilePath => Path.Combine(DirectoryPath, "recorder.log");

    public static void Info(string message)
    {
        Write("INFO", message);
    }

    public static void Error(string message, Exception? exception = null)
    {
        Write("ERROR", exception is null ? message : $"{message}{Environment.NewLine}{exception}");
    }

    private static void Write(string level, string message)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);

            lock (Lock)
            {
                File.AppendAllText(
                    FilePath,
                    $"{DateTimeOffset.Now:O} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging should never prevent the tray app from starting.
        }
    }
}
