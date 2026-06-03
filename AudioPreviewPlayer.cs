using NAudio.Wave;

namespace Recorder;

public sealed class AudioPreviewPlayer : IDisposable
{
    private WaveOutEvent? output;
    private AudioFileReader? reader;
    private System.Windows.Forms.Timer? stopTimer;

    public void Play(string audioPath, double startSeconds, double endSeconds)
    {
        Stop();

        if (!File.Exists(audioPath))
        {
            throw new FileNotFoundException("Audio file was not found.", audioPath);
        }

        reader = new AudioFileReader(audioPath)
        {
            CurrentTime = TimeSpan.FromSeconds(Math.Max(0, startSeconds))
        };

        output = new WaveOutEvent();
        output.Init(reader);
        output.Play();

        var duration = Math.Max(0.25, endSeconds - startSeconds);
        stopTimer = new System.Windows.Forms.Timer
        {
            Interval = Math.Max(250, (int)Math.Ceiling(duration * 1000))
        };
        stopTimer.Tick += (_, _) => Stop();
        stopTimer.Start();
    }

    public void Stop()
    {
        stopTimer?.Stop();
        stopTimer?.Dispose();
        stopTimer = null;

        output?.Stop();
        output?.Dispose();
        output = null;

        reader?.Dispose();
        reader = null;
    }

    public void Dispose()
    {
        Stop();
    }
}
