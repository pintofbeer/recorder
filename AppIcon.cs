using System.Reflection;

namespace Recorder;

public static class AppIcon
{
    private const string ResourceName = "Recorder.Resources.AppIcon.ico";

    public static Icon Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            AppLog.Info($"Icon resource {ResourceName} was not found. Falling back to the system application icon.");
            return (Icon)SystemIcons.Application.Clone();
        }

        using var icon = new Icon(stream);
        return (Icon)icon.Clone();
    }
}
