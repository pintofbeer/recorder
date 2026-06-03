using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Recorder;

public sealed class MeetingComposer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public async Task<string?> ComposeAsync(
        string audioFilePath,
        DiarizationResult diarizationResult,
        RecordingFiles? recordingFiles = null,
        CancellationToken cancellationToken = default)
    {
        if (diarizationResult.JsonPath is null || !File.Exists(diarizationResult.JsonPath))
        {
            return null;
        }

        var transcript = await ReadTranscriptAsync(audioFilePath, cancellationToken);
        if (transcript is null)
        {
            return null;
        }

        var diarization = await ReadJsonAsync<DiarizationDocument>(diarizationResult.JsonPath, cancellationToken);
        var voiceprints = diarizationResult.VoiceprintsPath is not null && File.Exists(diarizationResult.VoiceprintsPath)
            ? await ReadJsonAsync<VoiceprintsDocument>(diarizationResult.VoiceprintsPath, cancellationToken)
            : new VoiceprintsDocument(null, null, null, []);

        var diarizationSegments = diarization.ExclusiveSegments.Count > 0
            ? diarization.ExclusiveSegments
            : diarization.Segments;

        var speakers = voiceprints.Voiceprints
            .Select(voiceprint => new MeetingSpeaker(
                voiceprint.Speaker,
                null,
                null,
                voiceprint.Dimension,
                voiceprint.Duration,
                voiceprint.SegmentCount,
                voiceprint.Embedding,
                voiceprint.Segments,
                null,
                "unresolved"))
            .ToList();

        foreach (var speakerId in diarizationSegments.Select(segment => segment.Speaker).Distinct())
        {
            if (speakers.All(speaker => speaker.Id != speakerId))
            {
                speakers.Add(new MeetingSpeaker(speakerId, null, null, null, null, null, null, null, null, "unresolved"));
            }
        }

        var transcriptSegments = transcript.Segments
            .Select(segment =>
            {
                var speaker = AssignSpeaker(segment, diarizationSegments);
                return new MeetingTranscriptSegment(
                    segment.Start,
                    segment.End,
                    speaker?.Speaker,
                    null,
                    null,
                    speaker?.Confidence,
                    null,
                    "unresolved",
                    segment.Text);
            })
            .ToList();

        var document = new MeetingDocument(
            "recorder.meeting.v1",
            audioFilePath,
            CreateTracks(audioFilePath, recordingFiles),
            DateTimeOffset.UtcNow,
            new MeetingModels(diarization.Model, voiceprints.EmbeddingModel),
            speakers,
            transcriptSegments,
            diarizationSegments);

        var outputPath = Path.ChangeExtension(audioFilePath, ".meeting.json");
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(document, JsonOptions), cancellationToken);
        return outputPath;
    }

    private static IReadOnlyList<MeetingAudioTrack> CreateTracks(string audioFilePath, RecordingFiles? recordingFiles)
    {
        if (recordingFiles is null)
        {
            return [new MeetingAudioTrack("mixed", audioFilePath, "Processing audio used for transcription and diarization.")];
        }

        var tracks = new List<MeetingAudioTrack>
        {
            new("output", recordingFiles.OutputPath, "System output audio."),
            new("processing", recordingFiles.ProcessingPath, recordingFiles.MicrophonePath is null
                ? "System output audio used for transcription and diarization."
                : "Merged system output and microphone audio used for transcription and diarization.")
        };

        if (recordingFiles.MicrophonePath is not null)
        {
            tracks.Insert(1, new MeetingAudioTrack("microphone", recordingFiles.MicrophonePath, "Local microphone audio."));
        }

        return tracks;
    }

    private static async Task<T> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException($"Could not read {path}");
    }

    private static async Task<TranscriptDocument?> ReadTranscriptAsync(string audioFilePath, CancellationToken cancellationToken)
    {
        var transcriptJsonPath = Path.ChangeExtension(audioFilePath, ".transcript.json");
        if (File.Exists(transcriptJsonPath))
        {
            return await ReadJsonAsync<TranscriptDocument>(transcriptJsonPath, cancellationToken);
        }

        var transcriptTextPath = Path.ChangeExtension(audioFilePath, ".txt");
        if (!File.Exists(transcriptTextPath))
        {
            return null;
        }

        var segments = new List<TranscriptSegment>();
        var pattern = new Regex(@"^\[(?<start>[^\]]+)\s+-\s+(?<end>[^\]]+)\]\s+(?<text>.*)$");

        foreach (var line in await File.ReadAllLinesAsync(transcriptTextPath, cancellationToken))
        {
            var match = pattern.Match(line);
            if (!match.Success)
            {
                continue;
            }

            segments.Add(new TranscriptSegment(
                ParseTimestamp(match.Groups["start"].Value),
                ParseTimestamp(match.Groups["end"].Value),
                match.Groups["text"].Value.Trim()));
        }

        return new TranscriptDocument(audioFilePath, segments);
    }

    private static double ParseTimestamp(string value)
    {
        var parts = value.Split(':').Select(double.Parse).ToArray();
        return parts.Length switch
        {
            2 => (parts[0] * 60) + parts[1],
            3 => (parts[0] * 3600) + (parts[1] * 60) + parts[2],
            _ => 0
        };
    }

    private static SpeakerAssignment? AssignSpeaker(TranscriptSegment transcript, IReadOnlyList<DiarizationSegment> speakers)
    {
        var transcriptDuration = Math.Max(0.001, transcript.End - transcript.Start);

        var best = speakers
            .Select(segment => new
            {
                segment.Speaker,
                Overlap = Overlap(transcript.Start, transcript.End, segment.Start, segment.End)
            })
            .Where(candidate => candidate.Overlap > 0)
            .GroupBy(candidate => candidate.Speaker)
            .Select(group => new SpeakerAssignment(
                group.Key,
                Math.Round(Math.Min(1, group.Sum(candidate => candidate.Overlap) / transcriptDuration), 3)))
            .OrderByDescending(candidate => candidate.Confidence)
            .FirstOrDefault();

        if (best is not null)
        {
            return best;
        }

        var nearest = speakers
            .Select(segment => new
            {
                segment.Speaker,
                Distance = Math.Min(Math.Abs(transcript.Start - segment.End), Math.Abs(transcript.End - segment.Start))
            })
            .OrderBy(candidate => candidate.Distance)
            .FirstOrDefault();

        return nearest is null || nearest.Distance > 1.5
            ? null
            : new SpeakerAssignment(nearest.Speaker, 0);
    }

    private static double Overlap(double firstStart, double firstEnd, double secondStart, double secondEnd)
    {
        return Math.Max(0, Math.Min(firstEnd, secondEnd) - Math.Max(firstStart, secondStart));
    }
}

public sealed record DiarizationDocument(
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("audio")] string? Audio,
    [property: JsonPropertyName("segments")] IReadOnlyList<DiarizationSegment> Segments,
    [property: JsonPropertyName("exclusiveSegments")] IReadOnlyList<DiarizationSegment> ExclusiveSegments);

public sealed record DiarizationSegment(
    [property: JsonPropertyName("start")] double Start,
    [property: JsonPropertyName("end")] double End,
    [property: JsonPropertyName("speaker")] string Speaker);

public sealed record VoiceprintsDocument(
    [property: JsonPropertyName("embeddingModel")] string? EmbeddingModel,
    [property: JsonPropertyName("audio")] string? Audio,
    [property: JsonPropertyName("sampleRate")] int? SampleRate,
    [property: JsonPropertyName("voiceprints")] IReadOnlyList<Voiceprint> Voiceprints);

public sealed record Voiceprint(
    [property: JsonPropertyName("speaker")] string Speaker,
    [property: JsonPropertyName("dimension")] int Dimension,
    [property: JsonPropertyName("duration")] double Duration,
    [property: JsonPropertyName("segmentCount")] int SegmentCount,
    [property: JsonPropertyName("segments")] IReadOnlyList<VoiceprintSegment> Segments,
    [property: JsonPropertyName("embedding")] IReadOnlyList<double> Embedding);

public sealed record VoiceprintSegment(
    [property: JsonPropertyName("start")] double Start,
    [property: JsonPropertyName("end")] double End,
    [property: JsonPropertyName("duration")] double Duration);

public sealed record MeetingDocument
{
    [JsonConstructor]
    public MeetingDocument(
        string schema,
        string audio,
        IReadOnlyList<MeetingAudioTrack>? tracks,
        DateTimeOffset createdAt,
        MeetingModels models,
        IReadOnlyList<MeetingSpeaker> speakers,
        IReadOnlyList<MeetingTranscriptSegment> transcript,
        IReadOnlyList<DiarizationSegment> diarization)
    {
        Schema = schema;
        Audio = audio;
        Tracks = tracks ?? [new MeetingAudioTrack("mixed", audio, "Processing audio used for transcription and diarization.")];
        CreatedAt = createdAt;
        Models = models;
        Speakers = speakers;
        Transcript = transcript;
        Diarization = diarization;
    }

    [JsonPropertyName("schema")]
    public string Schema { get; init; }

    [JsonPropertyName("audio")]
    public string Audio { get; init; }

    [JsonPropertyName("tracks")]
    public IReadOnlyList<MeetingAudioTrack> Tracks { get; init; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("models")]
    public MeetingModels Models { get; init; }

    [JsonPropertyName("speakers")]
    public IReadOnlyList<MeetingSpeaker> Speakers { get; init; }

    [JsonPropertyName("transcript")]
    public IReadOnlyList<MeetingTranscriptSegment> Transcript { get; init; }

    [JsonPropertyName("diarization")]
    public IReadOnlyList<DiarizationSegment> Diarization { get; init; }
}

public sealed record MeetingAudioTrack(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("description")] string Description);

public sealed record MeetingModels(
    [property: JsonPropertyName("diarization")] string? Diarization,
    [property: JsonPropertyName("embedding")] string? Embedding);

public sealed record MeetingSpeaker(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("resolvedSpeakerId")] string? ResolvedSpeakerId,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("embeddingDimension")] int? EmbeddingDimension,
    [property: JsonPropertyName("voiceprintDuration")] double? VoiceprintDuration,
    [property: JsonPropertyName("voiceprintSegmentCount")] int? VoiceprintSegmentCount,
    [property: JsonPropertyName("embedding")] IReadOnlyList<double>? Embedding,
    [property: JsonPropertyName("voiceprintSegments")] IReadOnlyList<VoiceprintSegment>? VoiceprintSegments,
    [property: JsonPropertyName("speakerMatchConfidence")] double? SpeakerMatchConfidence,
    [property: JsonPropertyName("speakerMatchStatus")] string? SpeakerMatchStatus);

public sealed record MeetingTranscriptSegment(
    [property: JsonPropertyName("start")] double Start,
    [property: JsonPropertyName("end")] double End,
    [property: JsonPropertyName("speakerId")] string? SpeakerId,
    [property: JsonPropertyName("resolvedSpeakerId")] string? ResolvedSpeakerId,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("speakerConfidence")] double? SpeakerConfidence,
    [property: JsonPropertyName("speakerMatchConfidence")] double? SpeakerMatchConfidence,
    [property: JsonPropertyName("speakerMatchStatus")] string? SpeakerMatchStatus,
    [property: JsonPropertyName("text")] string Text);

public sealed record SpeakerAssignment(string Speaker, double Confidence);
