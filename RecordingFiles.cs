using System.Text.Json.Serialization;

namespace Recorder;

public sealed record RecordingFiles(
    string AppDisplayName,
    string? AppProcessName,
    string OutputPath,
    string? MicrophonePath,
    string ProcessingPath,
    DateTimeOffset StartedAtUtc)
{
    public List<WindowTitleObservation> WindowTitleObservations { get; } = [];
}

public sealed record WindowTitleObservation(
    [property: JsonPropertyName("observedAt")] DateTimeOffset ObservedAt,
    [property: JsonPropertyName("offsetSeconds")] double OffsetSeconds,
    [property: JsonPropertyName("processName")] string ProcessName,
    [property: JsonPropertyName("title")] string Title);
