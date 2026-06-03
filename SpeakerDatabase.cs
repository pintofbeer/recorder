using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Recorder;

public sealed class SpeakerDatabase
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly Func<AppSettings> settingsProvider;

    public SpeakerDatabase(Func<AppSettings> settingsProvider)
    {
        this.settingsProvider = settingsProvider;
        Directory.CreateDirectory(DatabaseDirectory);
        Initialize();
    }

    public static string DatabaseDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LaptopOutputRecorder");

    public static string DatabasePath => Path.Combine(DatabaseDirectory, "speakers.db");

    public IReadOnlyList<KnownSpeaker> GetSpeakers()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, display_name FROM speakers ORDER BY display_name COLLATE NOCASE";

        var speakers = new List<KnownSpeaker>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            speakers.Add(new KnownSpeaker(reader.GetString(0), reader.GetString(1)));
        }

        return speakers;
    }

    public string UpsertSpeakerByName(string displayName)
    {
        var trimmed = displayName.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new ArgumentException("Speaker name is required.", nameof(displayName));
        }

        using var connection = OpenConnection();
        using var lookup = connection.CreateCommand();
        lookup.CommandText = "SELECT id FROM speakers WHERE lower(display_name) = lower($name)";
        lookup.Parameters.AddWithValue("$name", trimmed);

        var existingId = lookup.ExecuteScalar() as string;
        if (!string.IsNullOrWhiteSpace(existingId))
        {
            return existingId;
        }

        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var id = Guid.NewGuid().ToString("N");

        using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO speakers (id, display_name, created_at, updated_at)
            VALUES ($id, $name, $createdAt, $updatedAt)
            """;
        insert.Parameters.AddWithValue("$id", id);
        insert.Parameters.AddWithValue("$name", trimmed);
        insert.Parameters.AddWithValue("$createdAt", now);
        insert.Parameters.AddWithValue("$updatedAt", now);
        insert.ExecuteNonQuery();

        return id;
    }

    public void AddEmbedding(
        string speakerId,
        IReadOnlyList<double> embedding,
        string? sourceMeetingPath,
        string? sourceSpeakerId,
        double? durationSeconds)
    {
        if (embedding.Count == 0)
        {
            return;
        }

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO speaker_embeddings (
                id,
                speaker_id,
                source_meeting_path,
                source_speaker_id,
                embedding_json,
                dimension,
                duration_seconds,
                quality_score,
                created_at
            )
            VALUES (
                $id,
                $speakerId,
                $sourceMeetingPath,
                $sourceSpeakerId,
                $embeddingJson,
                $dimension,
                $durationSeconds,
                $qualityScore,
                $createdAt
            )
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$speakerId", speakerId);
        command.Parameters.AddWithValue("$sourceMeetingPath", (object?)sourceMeetingPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$sourceSpeakerId", (object?)sourceSpeakerId ?? DBNull.Value);
        command.Parameters.AddWithValue("$embeddingJson", JsonSerializer.Serialize(embedding));
        command.Parameters.AddWithValue("$dimension", embedding.Count);
        command.Parameters.AddWithValue("$durationSeconds", (object?)durationSeconds ?? DBNull.Value);
        command.Parameters.AddWithValue("$qualityScore", durationSeconds is null ? DBNull.Value : Math.Min(1.0, durationSeconds.Value / 30.0));
        command.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    public async Task<string> ResolveMeetingAsync(string meetingPath, CancellationToken cancellationToken = default)
    {
        var settings = settingsProvider().SpeakerMatching;
        if (!settings.Enabled)
        {
            return meetingPath;
        }

        var document = await ReadMeetingAsync(meetingPath, cancellationToken);
        var matches = document.Speakers
            .Where(speaker => speaker.Embedding is { Count: > 0 })
            .ToDictionary(speaker => speaker.Id, speaker => Match(speaker.Embedding!, settings));

        var speakers = document.Speakers
            .Select(speaker =>
            {
                if (string.Equals(speaker.SpeakerMatchStatus, "manual", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(speaker.ResolvedSpeakerId))
                {
                    return speaker;
                }

                matches.TryGetValue(speaker.Id, out var match);
                return speaker with
                {
                    ResolvedSpeakerId = match?.Status == SpeakerMatchStatus.Matched ? match.SpeakerId : speaker.ResolvedSpeakerId,
                    Name = match?.Status == SpeakerMatchStatus.Matched ? match.DisplayName : speaker.Name,
                    SpeakerMatchConfidence = match?.Score,
                    SpeakerMatchStatus = match?.Status.ToString().ToLowerInvariant() ?? "unresolved"
                };
            })
            .ToList();

        var speakerLookup = speakers.ToDictionary(speaker => speaker.Id);
        var transcript = document.Transcript
            .Select(segment =>
            {
                var speaker = segment.SpeakerId is not null && speakerLookup.TryGetValue(segment.SpeakerId, out var resolved)
                    ? resolved
                    : null;

                return segment with
                {
                    ResolvedSpeakerId = speaker?.ResolvedSpeakerId,
                    Name = speaker?.Name,
                    SpeakerMatchConfidence = speaker?.SpeakerMatchConfidence,
                    SpeakerMatchStatus = speaker?.SpeakerMatchStatus
                };
            })
            .ToList();

        var resolvedDocument = document with
        {
            Speakers = speakers,
            Transcript = transcript
        };

        await File.WriteAllTextAsync(meetingPath, JsonSerializer.Serialize(resolvedDocument, JsonOptions), cancellationToken);
        return meetingPath;
    }

    public async Task EnrollSpeakerFromMeetingAsync(
        string meetingPath,
        string meetingSpeakerId,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        var document = await ReadMeetingAsync(meetingPath, cancellationToken);
        var meetingSpeaker = document.Speakers.FirstOrDefault(speaker => speaker.Id == meetingSpeakerId)
            ?? throw new InvalidOperationException($"Speaker {meetingSpeakerId} was not found in {meetingPath}.");

        if (meetingSpeaker.Embedding is null || meetingSpeaker.Embedding.Count == 0)
        {
            throw new InvalidOperationException($"Speaker {meetingSpeakerId} does not have a voiceprint embedding.");
        }

        var speakerId = UpsertSpeakerByName(displayName);
        AddEmbedding(
            speakerId,
            meetingSpeaker.Embedding,
            meetingPath,
            meetingSpeaker.Id,
            meetingSpeaker.VoiceprintDuration);

        var speakerName = GetSpeakers().First(speaker => speaker.Id == speakerId).DisplayName;
        var updatedSpeakers = document.Speakers
            .Select(speaker => speaker.Id == meetingSpeakerId
                ? speaker with
                {
                    ResolvedSpeakerId = speakerId,
                    Name = speakerName,
                    SpeakerMatchConfidence = 1.0,
                    SpeakerMatchStatus = "manual"
                }
                : speaker)
            .ToList();

        var updatedTranscript = document.Transcript
            .Select(segment => segment.SpeakerId == meetingSpeakerId
                ? segment with
                {
                    ResolvedSpeakerId = speakerId,
                    Name = speakerName,
                    SpeakerMatchConfidence = 1.0,
                    SpeakerMatchStatus = "manual"
                }
                : segment)
            .ToList();

        var updatedDocument = document with
        {
            Speakers = updatedSpeakers,
            Transcript = updatedTranscript
        };

        await File.WriteAllTextAsync(meetingPath, JsonSerializer.Serialize(updatedDocument, JsonOptions), cancellationToken);
    }

    private SpeakerMatch? Match(IReadOnlyList<double> embedding, SpeakerMatchingSettings settings)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT e.speaker_id, s.display_name, e.embedding_json
            FROM speaker_embeddings e
            JOIN speakers s ON s.id = e.speaker_id
            WHERE e.dimension = $dimension
            """;
        command.Parameters.AddWithValue("$dimension", embedding.Count);

        var scores = new List<(string SpeakerId, string DisplayName, double Score)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var candidate = JsonSerializer.Deserialize<double[]>(reader.GetString(2)) ?? [];
            if (candidate.Length != embedding.Count)
            {
                continue;
            }

            scores.Add((reader.GetString(0), reader.GetString(1), CosineSimilarity(embedding, candidate)));
        }

        var ranked = scores
            .GroupBy(score => new { score.SpeakerId, score.DisplayName })
            .Select(group => new
            {
                group.Key.SpeakerId,
                group.Key.DisplayName,
                Score = group.OrderByDescending(value => value.Score).Take(3).Average(value => value.Score)
            })
            .OrderByDescending(score => score.Score)
            .ToList();

        if (ranked.Count == 0)
        {
            return null;
        }

        var best = ranked[0];
        var second = ranked.Count > 1 ? ranked[1] : null;
        var status = best.Score >= settings.Threshold
            ? second is not null && best.Score - second.Score < settings.AmbiguousMargin
                ? SpeakerMatchStatus.Ambiguous
                : SpeakerMatchStatus.Matched
            : SpeakerMatchStatus.Unresolved;

        return new SpeakerMatch(best.SpeakerId, best.DisplayName, Math.Round(best.Score, 4), status);
    }

    private static double CosineSimilarity(IReadOnlyList<double> first, IReadOnlyList<double> second)
    {
        double dot = 0;
        double firstNorm = 0;
        double secondNorm = 0;

        for (var index = 0; index < first.Count; index++)
        {
            dot += first[index] * second[index];
            firstNorm += first[index] * first[index];
            secondNorm += second[index] * second[index];
        }

        return firstNorm == 0 || secondNorm == 0
            ? 0
            : dot / (Math.Sqrt(firstNorm) * Math.Sqrt(secondNorm));
    }

    private static async Task<MeetingDocument> ReadMeetingAsync(string meetingPath, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(meetingPath);
        return await JsonSerializer.DeserializeAsync<MeetingDocument>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException($"Could not read {meetingPath}.");
    }

    private static SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection($"Data Source={DatabasePath}");
        connection.Open();
        return connection;
    }

    private void Initialize()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS speakers (
                id TEXT PRIMARY KEY,
                display_name TEXT NOT NULL UNIQUE COLLATE NOCASE,
                notes TEXT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS speaker_embeddings (
                id TEXT PRIMARY KEY,
                speaker_id TEXT NOT NULL,
                source_meeting_path TEXT NULL,
                source_speaker_id TEXT NULL,
                embedding_json TEXT NOT NULL,
                dimension INTEGER NOT NULL,
                duration_seconds REAL NULL,
                quality_score REAL NULL,
                created_at TEXT NOT NULL,
                FOREIGN KEY (speaker_id) REFERENCES speakers(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_speaker_embeddings_speaker_id
                ON speaker_embeddings (speaker_id);

            CREATE INDEX IF NOT EXISTS idx_speaker_embeddings_dimension
                ON speaker_embeddings (dimension);
            """;
        command.ExecuteNonQuery();
    }
}

public sealed record KnownSpeaker(string Id, string DisplayName);

public sealed record SpeakerMatch(string SpeakerId, string DisplayName, double Score, SpeakerMatchStatus Status);

public enum SpeakerMatchStatus
{
    Matched,
    Ambiguous,
    Unresolved
}
