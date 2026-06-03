using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Whisper.net;
using Whisper.net.Ggml;

namespace Recorder;

public sealed class TranscriptionService
{
    private readonly SemaphoreSlim transcriptionLock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public async Task<string> TranscribeAsync(string audioFilePath, CancellationToken cancellationToken = default)
    {
        await transcriptionLock.WaitAsync(cancellationToken);

        try
        {
            var modelPath = await EnsureModelAsync(cancellationToken);
            var transcriptPath = Path.ChangeExtension(audioFilePath, ".txt");
            var transcriptJsonPath = Path.ChangeExtension(audioFilePath, ".transcript.json");
            var transcript = new StringBuilder();
            var segments = new List<TranscriptSegment>();

            using var whisperFactory = WhisperFactory.FromPath(modelPath);
            using var processor = whisperFactory.CreateBuilder()
                .WithLanguage("auto")
                .Build();

            var transcriptionInputPath = CreateTranscriptionInputFile(audioFilePath);
            try
            {
                await using var fileStream = File.OpenRead(transcriptionInputPath);
                await foreach (var result in processor.ProcessAsync(fileStream, cancellationToken))
                {
                    var text = result.Text.Trim();
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        continue;
                    }

                    transcript.AppendLine($"[{FormatTimestamp(result.Start)} - {FormatTimestamp(result.End)}] {text}");
                    segments.Add(new TranscriptSegment(
                        Math.Round(result.Start.TotalSeconds, 3),
                        Math.Round(result.End.TotalSeconds, 3),
                        text));
                }
            }
            finally
            {
                TryDelete(transcriptionInputPath);
            }

            await File.WriteAllTextAsync(transcriptPath, transcript.ToString(), cancellationToken);
            await File.WriteAllTextAsync(
                transcriptJsonPath,
                JsonSerializer.Serialize(new TranscriptDocument(audioFilePath, segments), JsonOptions),
                cancellationToken);
            return transcriptPath;
        }
        finally
        {
            transcriptionLock.Release();
        }
    }

    private static async Task<string> EnsureModelAsync(CancellationToken cancellationToken)
    {
        var modelDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LaptopOutputRecorder",
            "Models");
        Directory.CreateDirectory(modelDirectory);

        var modelPath = Path.Combine(modelDirectory, "ggml-base.bin");
        if (File.Exists(modelPath))
        {
            return modelPath;
        }

        var temporaryPath = $"{modelPath}.download";
        await using (var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(GgmlType.Base))
        await using (var fileWriter = File.Open(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await modelStream.CopyToAsync(fileWriter, cancellationToken);
        }

        File.Move(temporaryPath, modelPath, overwrite: true);
        return modelPath;
    }

    private static string CreateTranscriptionInputFile(string audioFilePath)
    {
        var outputPath = Path.Combine(
            Path.GetTempPath(),
            $"recorder-whisper-{Guid.NewGuid():N}.wav");

        using var reader = new AudioFileReader(audioFilePath);
        ISampleProvider sampleProvider = reader;

        if (reader.WaveFormat.Channels == 2)
        {
            sampleProvider = new StereoToMonoSampleProvider(sampleProvider)
            {
                LeftVolume = 0.5f,
                RightVolume = 0.5f
            };
        }

        var resampled = new WdlResamplingSampleProvider(sampleProvider, 16000);
        WaveFileWriter.CreateWaveFile16(outputPath, resampled);
        return outputPath;
    }

    private static string FormatTimestamp(TimeSpan timestamp)
    {
        return timestamp.Hours > 0
            ? timestamp.ToString(@"hh\:mm\:ss")
            : timestamp.ToString(@"mm\:ss");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Temporary transcription files are best-effort cleanup only.
        }
    }
}

public sealed record TranscriptDocument(
    [property: JsonPropertyName("audio")] string Audio,
    [property: JsonPropertyName("segments")] IReadOnlyList<TranscriptSegment> Segments);

public sealed record TranscriptSegment(
    [property: JsonPropertyName("start")] double Start,
    [property: JsonPropertyName("end")] double End,
    [property: JsonPropertyName("text")] string Text);
