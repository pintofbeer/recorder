namespace Recorder;

public sealed record DiarizationResult(
    bool Success,
    bool Skipped,
    string? JsonPath,
    string? RttmPath,
    string? TextPath,
    string? VoiceprintsPath,
    string Message)
{
    public static DiarizationResult Completed(string jsonPath, string rttmPath, string textPath, string voiceprintsPath) =>
        new(true, false, jsonPath, rttmPath, textPath, voiceprintsPath, "Diarization finished.");

    public static DiarizationResult CreateSkipped(string message) =>
        new(false, true, null, null, null, null, message);

    public static DiarizationResult Failed(string message) =>
        new(false, false, null, null, null, null, message);
}
