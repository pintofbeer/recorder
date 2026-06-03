using System.Text.Json;

namespace Recorder;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public string SettingsDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LaptopOutputRecorder");

    public string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    public AppSettings Load()
    {
        EnsureExists();

        AppSettings settings;
        try
        {
            using var stream = File.OpenRead(SettingsPath);
            settings = JsonSerializer.Deserialize<AppSettings>(stream, JsonOptions) ?? new AppSettings();
        }
        catch (JsonException ex)
        {
            AppLog.Error($"Could not parse settings file at {SettingsPath}. Starting with defaults.", ex);
            settings = new AppSettings();
        }

        settings.WatchedApps = settings.WatchedApps
            .Where(app => app.ProcessNames.Length > 0)
            .ToList();

        if (settings.WatchedApps.Count == 0)
        {
            settings.WatchedApps = AppSettings.DefaultWatchedApps();
        }

        settings.WatchedApps = settings.WatchedApps
            .Select(UpgradeWatchedAppDefaults)
            .ToList();

        Save(settings);
        return settings;
    }

    private static WatchedApp UpgradeWatchedAppDefaults(WatchedApp app)
    {
        if (app.CaptureMicrophone)
        {
            return app;
        }

        var isDefaultMeetingApp =
            string.Equals(app.DisplayName, "Zoom", StringComparison.OrdinalIgnoreCase)
            || string.Equals(app.DisplayName, "Microsoft Teams", StringComparison.OrdinalIgnoreCase);

        return isDefaultMeetingApp
            ? app with { CaptureMicrophone = true }
            : app;
    }

    private void Save(AppSettings settings)
    {
        using var stream = File.Create(SettingsPath);
        JsonSerializer.Serialize(stream, settings, JsonOptions);
    }

    private void EnsureExists()
    {
        Directory.CreateDirectory(SettingsDirectory);

        if (File.Exists(SettingsPath))
        {
            return;
        }

        using var stream = File.Create(SettingsPath);
        JsonSerializer.Serialize(stream, new AppSettings(), JsonOptions);
    }
}
