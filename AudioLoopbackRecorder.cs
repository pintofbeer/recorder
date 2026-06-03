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

    public bool IsRecording => outputCapture is not null;
    public RecordingFiles? CurrentFiles => currentFiles;

    public RecordingFiles Start(string displayName, bool captureMicrophone)
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
            $"{basePath}.wav",
            captureMicrophone ? $"{basePath}.mic.wav" : null,
            captureMicrophone ? $"{basePath}.merged.wav" : $"{basePath}.wav");

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

        outputCapture.DataAvailable += (_, args) =>
        {
            outputWriter?.Write(args.Buffer, 0, args.BytesRecorded);
            outputWriter?.Flush();
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

        microphoneCapture.DataAvailable += (_, args) =>
        {
            microphoneWriter?.Write(args.Buffer, 0, args.BytesRecorded);
            microphoneWriter?.Flush();
        };

        microphoneCapture.RecordingStopped += (_, _) =>
        {
            microphoneWriter?.Dispose();
            microphoneWriter = null;
        };
        microphoneCapture.StartRecording();
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

        currentFiles = null;
    }
}
