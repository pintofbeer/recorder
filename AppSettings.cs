using System.Text.Json.Serialization;

namespace Recorder;

public sealed class AppSettings
{
    [JsonPropertyName("watchedApps")]
    public List<WatchedApp> WatchedApps { get; set; } = DefaultWatchedApps();

    [JsonPropertyName("diarization")]
    public DiarizationSettings Diarization { get; set; } = new();

    [JsonPropertyName("speakerMatching")]
    public SpeakerMatchingSettings SpeakerMatching { get; set; } = new();

    public static List<WatchedApp> DefaultWatchedApps() =>
    [
        new("Zoom", ["Zoom"], true),
        new("Microsoft Teams", ["Teams", "MSTeams", "ms-teams"], true)
    ];
}

public sealed class SpeakerMatchingSettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("threshold")]
    public double Threshold { get; set; } = 0.85;

    [JsonPropertyName("ambiguousMargin")]
    public double AmbiguousMargin { get; set; } = 0.03;
}

public sealed record WatchedApp(
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("processNames")] string[] ProcessNames,
    [property: JsonPropertyName("captureMicrophone")] bool CaptureMicrophone = false);

public sealed class DiarizationSettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("pythonExecutable")]
    public string PythonExecutable { get; set; } = "python";

    [JsonPropertyName("huggingFaceToken")]
    public string HuggingFaceToken { get; set; } = "";

    [JsonPropertyName("model")]
    public string Model { get; set; } = "pyannote/speaker-diarization-community-1";

    [JsonPropertyName("embeddingModel")]
    public string EmbeddingModel { get; set; } = "pyannote/embedding";

    [JsonPropertyName("device")]
    public string Device { get; set; } = "auto";
}
