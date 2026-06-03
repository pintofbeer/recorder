using System.Diagnostics;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Recorder;

public sealed class AudioLoopbackRecorder : IDisposable
{
    private WasapiLoopbackCapture? outputCapture;
    private WaveInEvent? microphoneCapture;
    private WaveFileWriter? outputWriter;
    private WaveFileWriter? microphoneWriter;
    private RecordingFiles? currentFiles;
    private Stopwatch? recordingClock;
    private CaptureTimeline? outputTimeline;
    private CaptureTimeline? microphoneTimeline;

    public bool IsRecording => outputCapture is not null;
    public RecordingFiles? CurrentFiles => currentFiles;

    public RecordingFiles Start(string displayName, bool captureMicrophone, string? processName = null)
    {
        if (IsRecording)
        {
            throw new InvalidOperationException("Recording is already active.");
        }

        var recordingsDirectory = GetRecordingsDirectory();
        Directory.CreateDirectory(recordingsDirectory);

        var basePath = Path.Combine(
            recordingsDirectory,
            $"{SanitizeFileName(displayName)}-{DateTime.Now:yyyyMMdd-HHmmss}");

        currentFiles = new RecordingFiles(
            displayName,
            processName,
            $"{basePath}.wav",
            captureMicrophone ? $"{basePath}.mic.wav" : null,
            captureMicrophone ? $"{basePath}.merged.wav" : $"{basePath}.wav",
            DateTimeOffset.UtcNow);

        recordingClock = Stopwatch.StartNew();
        StartOutputCapture(currentFiles.OutputPath);

        if (captureMicrophone && WaveIn.DeviceCount > 0)
        {
            StartMicrophoneCapture(currentFiles.MicrophonePath!);
        }
        else if (captureMicrophone)
        {
            AppLog.Info("Microphone capture was requested, but no input devices were available.");
        }

        return currentFiles;
    }

    public RecordingFiles? Stop()
    {
        var stoppedFiles = currentFiles;

        if (outputCapture is null)
        {
            return stoppedFiles;
        }

        outputCapture.StopRecording();
        microphoneCapture?.StopRecording();
        DisposeCapture();

        if (stoppedFiles?.MicrophonePath is not null && File.Exists(stoppedFiles.MicrophonePath))
        {
            MergeToMono(stoppedFiles.OutputPath, stoppedFiles.MicrophonePath, stoppedFiles.ProcessingPath);
        }

        return stoppedFiles;
    }

    public void Dispose()
    {
        Stop();
    }

    private void StartOutputCapture(string outputPath)
    {
        outputCapture = new WasapiLoopbackCapture();
        outputWriter = new WaveFileWriter(outputPath, outputCapture.WaveFormat);
        outputTimeline = new CaptureTimeline("system output", outputCapture.WaveFormat);

        outputCapture.DataAvailable += (_, args) =>
        {
            WriteAligned(outputWriter, outputTimeline, args.Buffer, args.BytesRecorded);
        };

        outputCapture.RecordingStopped += (_, _) =>
        {
            outputWriter?.Dispose();
            outputWriter = null;
        };
        outputCapture.StartRecording();
    }

    private void StartMicrophoneCapture(string microphonePath)
    {
        microphoneCapture = new WaveInEvent
        {
            DeviceNumber = 0,
            WaveFormat = new WaveFormat(16000, 16, 1)
        };
        microphoneWriter = new WaveFileWriter(microphonePath, microphoneCapture.WaveFormat);
        microphoneTimeline = new CaptureTimeline("microphone", microphoneCapture.WaveFormat);

        microphoneCapture.DataAvailable += (_, args) =>
        {
            WriteAligned(microphoneWriter, microphoneTimeline, args.Buffer, args.BytesRecorded);
        };

        microphoneCapture.RecordingStopped += (_, _) =>
        {
            microphoneWriter?.Dispose();
            microphoneWriter = null;
        };
        microphoneCapture.StartRecording();
    }

    private void WriteAligned(WaveFileWriter? writer, CaptureTimeline? timeline, byte[] buffer, int bytesRecorded)
    {
        if (writer is null || timeline is null || recordingClock is null || bytesRecorded <= 0)
        {
            return;
        }

        lock (timeline.SyncRoot)
        {
            var packetDuration = timeline.BytesToSeconds(bytesRecorded);
            var packetStart = Math.Max(0, recordingClock.Elapsed.TotalSeconds - packetDuration);
            var currentTimelinePosition = timeline.BytesToSeconds(timeline.BytesWritten);
            var missingSeconds = packetStart - currentTimelinePosition;

            if (timeline.BytesWritten == 0 || missingSeconds > 0.05)
            {
                var silenceBytes = timeline.SecondsToAlignedBytes(Math.Max(0, missingSeconds));
                if (silenceBytes > 0)
                {
                    WriteSilence(writer, silenceBytes);
                    timeline.BytesWritten += silenceBytes;
                    AppLog.Info($"Inserted {timeline.BytesToSeconds(silenceBytes):0.000}s of alignment silence into {timeline.Name} track.");
                }
            }

            writer.Write(buffer, 0, bytesRecorded);
            writer.Flush();
            timeline.BytesWritten += bytesRecorded;
        }
    }

    private static void WriteSilence(WaveFileWriter writer, int byteCount)
    {
        var silence = new byte[Math.Min(byteCount, 64 * 1024)];
        var remaining = byteCount;
        while (remaining > 0)
        {
            var chunk = Math.Min(remaining, silence.Length);
            writer.Write(silence, 0, chunk);
            remaining -= chunk;
        }
    }

    private static void MergeToMono(string outputPath, string microphonePath, string mergedPath)
    {
        using var outputReader = new AudioFileReader(outputPath);
        using var microphoneReader = new AudioFileReader(microphonePath);

        var outputProvider = ToMono(outputReader);
        var microphoneProvider = ToMono(microphoneReader);
        var outputResampled = new WdlResamplingSampleProvider(outputProvider, 16000);
        var microphoneResampled = new WdlResamplingSampleProvider(microphoneProvider, 16000);

        var mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(16000, 1));
        mixer.AddMixerInput(outputResampled);
        mixer.AddMixerInput(microphoneResampled);

        WaveFileWriter.CreateWaveFile16(mergedPath, mixer);
    }

    private static ISampleProvider ToMono(AudioFileReader reader)
    {
        ISampleProvider provider = reader;
        return reader.WaveFormat.Channels switch
        {
            1 => provider,
            2 => new StereoToMonoSampleProvider(provider)
            {
                LeftVolume = 0.5f,
                RightVolume = 0.5f
            },
            _ => new MultiplexingSampleProvider([provider], 1)
        };
    }

    private static string GetRecordingsDirectory()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, "My Recordings");
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "Recording" : sanitized;
    }

    private void DisposeCapture()
    {
        outputWriter?.Dispose();
        outputWriter = null;

        microphoneWriter?.Dispose();
        microphoneWriter = null;

        outputCapture?.Dispose();
        outputCapture = null;

        microphoneCapture?.Dispose();
        microphoneCapture = null;

        recordingClock = null;
        outputTimeline = null;
        microphoneTimeline = null;
        currentFiles = null;
    }

    private sealed class CaptureTimeline(string name, WaveFormat waveFormat)
    {
        public string Name { get; } = name;
        public WaveFormat WaveFormat { get; } = waveFormat;
        public object SyncRoot { get; } = new();
        public long BytesWritten { get; set; }

        public double BytesToSeconds(long byteCount)
        {
            return byteCount / (double)WaveFormat.AverageBytesPerSecond;
        }

        public int SecondsToAlignedBytes(double seconds)
        {
            if (seconds <= 0)
            {
                return 0;
            }

            var bytes = (long)Math.Round(seconds * WaveFormat.AverageBytesPerSecond);
            var blockAlign = Math.Max(1, WaveFormat.BlockAlign);
            bytes -= bytes % blockAlign;
            return bytes > int.MaxValue ? int.MaxValue : (int)bytes;
        }
    }
}
