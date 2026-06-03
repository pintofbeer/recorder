using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Recorder;

public sealed class DiarizationService
{
    private readonly SemaphoreSlim diarizationLock = new(1, 1);
    private readonly Func<AppSettings> settingsProvider;

    public DiarizationService(Func<AppSettings> settingsProvider)
    {
        this.settingsProvider = settingsProvider;
    }

    public async Task<DiarizationResult> DiarizeAsync(string audioFilePath, CancellationToken cancellationToken = default)
    {
        await diarizationLock.WaitAsync(cancellationToken);

        try
        {
            var settings = settingsProvider().Diarization;
            if (!settings.Enabled)
            {
                return DiarizationResult.CreateSkipped("Diarization is disabled in settings.");
            }

            var token = ResolveToken(settings);
            if (string.IsNullOrWhiteSpace(token))
            {
                return DiarizationResult.CreateSkipped("Set HF_TOKEN or diarization.huggingFaceToken to run pyannote.");
            }

            var scriptPath = Path.Combine(AppContext.BaseDirectory, "python", "diarize.py");
            if (!File.Exists(scriptPath))
            {
                scriptPath = Path.Combine(Application.StartupPath, "python", "diarize.py");
            }

            if (!File.Exists(scriptPath))
            {
                return DiarizationResult.Failed("Could not find python\\diarize.py next to the app.");
            }

            var outputJsonPath = Path.ChangeExtension(audioFilePath, ".diarization.json");
            var outputRttmPath = Path.ChangeExtension(audioFilePath, ".rttm");
            var outputTextPath = Path.ChangeExtension(audioFilePath, ".speakers.txt");
            var outputVoiceprintsPath = Path.ChangeExtension(audioFilePath, ".voiceprints.json");

            var result = await RunWorkerAsync(
                ResolvePythonExecutable(settings),
                scriptPath,
                audioFilePath,
                outputJsonPath,
                outputRttmPath,
                outputVoiceprintsPath,
                settings.Model,
                settings.EmbeddingModel,
                settings.Device,
                token,
                cancellationToken);

            if (!result.Success && !File.Exists(outputJsonPath))
            {
                return result;
            }

            await WriteSpeakerSummaryAsync(outputJsonPath, outputTextPath, cancellationToken);
            return DiarizationResult.Completed(outputJsonPath, outputRttmPath, outputTextPath, outputVoiceprintsPath);
        }
        finally
        {
            diarizationLock.Release();
        }
    }

    private static string ResolveToken(DiarizationSettings settings)
    {
        return !string.IsNullOrWhiteSpace(settings.HuggingFaceToken)
            ? settings.HuggingFaceToken
            : Environment.GetEnvironmentVariable("HF_TOKEN") ?? "";
    }

    private static string ResolvePythonExecutable(DiarizationSettings settings)
    {
        var configured = settings.PythonExecutable;
        var configuredIsDefault = string.IsNullOrWhiteSpace(configured)
            || string.Equals(configured, "python", StringComparison.OrdinalIgnoreCase);

        if (!configuredIsDefault)
        {
            return configured;
        }

        var appDataVenvPython = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LaptopOutputRecorder",
            "pyannote-venv",
            "Scripts",
            "python.exe");

        return File.Exists(appDataVenvPython) ? appDataVenvPython : "python";
    }

    private static async Task<DiarizationResult> RunWorkerAsync(
        string pythonExecutable,
        string scriptPath,
        string audioFilePath,
        string outputJsonPath,
        string outputRttmPath,
        string outputVoiceprintsPath,
        string model,
        string embeddingModel,
        string device,
        string token,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = string.IsNullOrWhiteSpace(pythonExecutable) ? "python" : pythonExecutable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("--audio");
        startInfo.ArgumentList.Add(audioFilePath);
        startInfo.ArgumentList.Add("--output-json");
        startInfo.ArgumentList.Add(outputJsonPath);
        startInfo.ArgumentList.Add("--output-rttm");
        startInfo.ArgumentList.Add(outputRttmPath);
        startInfo.ArgumentList.Add("--output-voiceprints");
        startInfo.ArgumentList.Add(outputVoiceprintsPath);
        startInfo.ArgumentList.Add("--model");
        startInfo.ArgumentList.Add(model);
        startInfo.ArgumentList.Add("--embedding-model");
        startInfo.ArgumentList.Add(embeddingModel);
        startInfo.ArgumentList.Add("--device");
        startInfo.ArgumentList.Add(string.IsNullOrWhiteSpace(device) ? "auto" : device);
        startInfo.Environment["HF_TOKEN"] = token;
        ConfigurePythonCacheEnvironment(startInfo);

        using var process = new Process { StartInfo = startInfo };
        var output = new StringBuilder();
        var error = new StringBuilder();

        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                output.AppendLine(args.Data);
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                error.AppendLine(args.Data);
            }
        };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return DiarizationResult.Failed($"Could not start Python: {ex.Message}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken);

        if (output.Length > 0)
        {
            AppLog.Info($"pyannote stdout:{Environment.NewLine}{output.ToString().Trim()}");
        }

        if (error.Length > 0)
        {
            AppLog.Info($"pyannote stderr:{Environment.NewLine}{error.ToString().Trim()}");
        }

        if (process.ExitCode != 0)
        {
            var message = GetUsefulFailureMessage(error.ToString(), output.ToString());
            return DiarizationResult.Failed(string.IsNullOrWhiteSpace(message) ? "pyannote failed." : message);
        }

        return DiarizationResult.Completed(outputJsonPath, outputRttmPath, "", outputVoiceprintsPath);
    }

    private static void ConfigurePythonCacheEnvironment(ProcessStartInfo startInfo)
    {
        var cacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LaptopOutputRecorder",
            "Cache");

        var matplotlibCache = Path.Combine(cacheRoot, "matplotlib");
        var huggingFaceCache = Path.Combine(cacheRoot, "huggingface");
        var torchCache = Path.Combine(cacheRoot, "torch");

        Directory.CreateDirectory(matplotlibCache);
        Directory.CreateDirectory(huggingFaceCache);
        Directory.CreateDirectory(torchCache);

        startInfo.Environment["MPLCONFIGDIR"] = matplotlibCache;
        startInfo.Environment["HF_HOME"] = huggingFaceCache;
        startInfo.Environment["HUGGINGFACE_HUB_CACHE"] = Path.Combine(huggingFaceCache, "hub");
        startInfo.Environment["TORCH_HOME"] = torchCache;
        startInfo.Environment["PYTHONUTF8"] = "1";
    }

    private static string GetUsefulFailureMessage(string stderr, string stdout)
    {
        var combined = string.Join(
            Environment.NewLine,
            new[] { stderr, stdout }.Where(text => !string.IsNullOrWhiteSpace(text)));

        var lines = combined
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => !IsNoisyPythonMessage(line))
            .ToList();

        if (lines.Count == 0)
        {
            return combined.Trim();
        }

        return string.Join(Environment.NewLine, lines.TakeLast(8));
    }

    private static bool IsNoisyPythonMessage(string line)
    {
        return line.Contains("matplotlib", StringComparison.OrdinalIgnoreCase)
            && line.Contains("font cache", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task WriteSpeakerSummaryAsync(
        string outputJsonPath,
        string outputTextPath,
        CancellationToken cancellationToken)
    {
        using var jsonStream = File.OpenRead(outputJsonPath);
        var document = await JsonDocument.ParseAsync(jsonStream, cancellationToken: cancellationToken);
        var segments = document.RootElement.GetProperty("exclusiveSegments");
        if (segments.GetArrayLength() == 0)
        {
            segments = document.RootElement.GetProperty("segments");
        }

        var summary = new StringBuilder();
        foreach (var segment in segments.EnumerateArray())
        {
            var start = TimeSpan.FromSeconds(segment.GetProperty("start").GetDouble());
            var end = TimeSpan.FromSeconds(segment.GetProperty("end").GetDouble());
            var speaker = segment.GetProperty("speaker").GetString() ?? "SPEAKER";
            summary.AppendLine($"[{FormatTimestamp(start)} - {FormatTimestamp(end)}] {speaker}");
        }

        await File.WriteAllTextAsync(outputTextPath, summary.ToString(), cancellationToken);
    }

    private static string FormatTimestamp(TimeSpan timestamp)
    {
        return timestamp.Hours > 0
            ? timestamp.ToString(@"hh\:mm\:ss\.fff")
            : timestamp.ToString(@"mm\:ss\.fff");
    }
}
