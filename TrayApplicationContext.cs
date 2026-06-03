using System.Diagnostics;

namespace Recorder;

public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly SettingsStore settingsStore = new();
    private readonly AudioLoopbackRecorder recorder = new();
    private readonly TranscriptionService transcriptionService = new();
    private readonly MeetingComposer meetingComposer = new();
    private readonly SpeakerDatabase speakerDatabase;
    private readonly DiarizationService diarizationService;
    private readonly ProcessMonitor processMonitor;
    private readonly TrayHostForm hostForm;
    private readonly NotifyIcon notifyIcon;
    private readonly System.Windows.Forms.Timer pollTimer;
    private readonly ToolStripMenuItem statusItem;
    private readonly ToolStripMenuItem watchedAppsItem;
    private readonly ToolStripMenuItem stopRecordingItem;
    private readonly Icon appIcon;
    private readonly Icon trayIcon;

    private AppSettings settings;
    private RecordingTarget? activeTarget;
    private RecordingFiles? activeRecordingFiles;
    private int activePostProcessingTasks;

    public TrayApplicationContext()
    {
        hostForm = new TrayHostForm();
        MainForm = hostForm;
        hostForm.CreateControl();

        AppLog.Info("Loading settings.");
        settings = settingsStore.Load();
        speakerDatabase = new SpeakerDatabase(() => settings);
        diarizationService = new DiarizationService(() => settings);
        processMonitor = new ProcessMonitor(() => settings);

        statusItem = new ToolStripMenuItem("Idle") { Enabled = false };
        watchedAppsItem = new ToolStripMenuItem();
        stopRecordingItem = new ToolStripMenuItem("Stop recording", null, (_, _) => StopRecording("Stopped manually"))
        {
            Enabled = false
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(statusItem);
        menu.Items.Add(watchedAppsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(stopRecordingItem);
        menu.Items.Add(new ToolStripMenuItem("Open My Recordings", null, (_, _) => OpenRecordingsFolder()));
        menu.Items.Add(new ToolStripMenuItem("Tag speakers...", null, (_, _) => TagSpeakers()));
        menu.Items.Add(new ToolStripMenuItem("Edit watched apps", null, (_, _) => OpenSettingsFile()));
        menu.Items.Add(new ToolStripMenuItem("Reload watched apps", null, (_, _) => ReloadSettings(showNotification: true)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => Exit()));

        appIcon = AppIcon.Load();
        trayIcon = new Icon(appIcon, SystemInformation.SmallIconSize);
        notifyIcon = new NotifyIcon
        {
            Icon = trayIcon,
            Text = "Laptop Output Recorder",
            ContextMenuStrip = menu,
            Visible = true
        };
        AppLog.Info("Tray icon created.");

        pollTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        pollTimer.Tick += (_, _) => Poll();
        pollTimer.Start();

        UpdateMenuState();
        ShowNotification("Recorder is running", "Right-click the tray icon for options.", ToolTipIcon.Info);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            pollTimer.Dispose();
            recorder.Dispose();
            notifyIcon.Dispose();
            hostForm.Dispose();
            trayIcon.Dispose();
            appIcon.Dispose();
        }

        base.Dispose(disposing);
    }

    private void Poll()
    {
        try
        {
            if (activeTarget is not null)
            {
                if (!processMonitor.IsStillOpenAndVisible(activeTarget))
                {
                    StopRecording($"{activeTarget.DisplayName} closed or minimized");
                }

                return;
            }

            var focusedApp = processMonitor.GetFocusedWatchedApp();
            if (focusedApp is not null)
            {
                StartRecording(focusedApp);
            }
        }
        catch (Exception ex)
        {
            ShowNotification("Recorder error", ex.Message, ToolTipIcon.Error);
        }
    }

    private void StartRecording(FocusedWatchedApp focusedApp)
    {
        activeTarget = new RecordingTarget(
            focusedApp.DisplayName,
            focusedApp.ProcessId,
            focusedApp.ProcessName,
            focusedApp.WindowHandle,
            focusedApp.CaptureMicrophone);

        activeRecordingFiles = recorder.Start(focusedApp.DisplayName, focusedApp.CaptureMicrophone);
        AppLog.Info($"Recording started for {focusedApp.DisplayName}: {activeRecordingFiles.OutputPath}. Microphone: {focusedApp.CaptureMicrophone}");
        ShowNotification("Recording started", $"{focusedApp.DisplayName}\n{activeRecordingFiles.OutputPath}", ToolTipIcon.Info);
        UpdateMenuState();
    }

    private void StopRecording(string reason)
    {
        if (activeTarget is null && !recorder.IsRecording)
        {
            return;
        }

        var stoppedTarget = activeTarget;
        var files = recorder.Stop();
        activeTarget = null;
        activeRecordingFiles = null;

        var title = stoppedTarget is null
            ? "Recording stopped"
            : $"{stoppedTarget.DisplayName} recording stopped";
        AppLog.Info($"{title}: {reason}. Files: {files}");
        ShowNotification(title, $"{reason}\n{files?.ProcessingPath}", ToolTipIcon.Info);
        UpdateMenuState();

        if (files is not null && File.Exists(files.ProcessingPath))
        {
            _ = TranscribeRecordingAsync(files);
        }
    }

    private void ReloadSettings(bool showNotification)
    {
        settings = settingsStore.Load();
        AppLog.Info("Settings reloaded.");
        UpdateMenuState();

        if (showNotification)
        {
            ShowNotification("Watched apps reloaded", WatchedAppsSummary(), ToolTipIcon.Info);
        }
    }

    private void OpenSettingsFile()
    {
        settingsStore.Load();
        Process.Start(new ProcessStartInfo("notepad.exe", settingsStore.SettingsPath)
        {
            UseShellExecute = true
        });
    }

    private void TagSpeakers()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Select meeting JSON",
            Filter = "Meeting JSON (*.meeting.json)|*.meeting.json|JSON files (*.json)|*.json|All files (*.*)|*.*",
            InitialDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "My Recordings"),
            CheckFileExists = true
        };

        if (dialog.ShowDialog() != DialogResult.OK)
        {
            return;
        }

        try
        {
            using var form = new SpeakerTaggingForm(speakerDatabase, dialog.FileName);
            if (form.ShowDialog() == DialogResult.OK)
            {
                ShowNotification("Speakers tagged", dialog.FileName, ToolTipIcon.Info);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error($"Speaker tagging failed for {dialog.FileName}.", ex);
            ShowNotification("Speaker tagging failed", ex.Message, ToolTipIcon.Error);
        }
    }

    private static void OpenRecordingsFolder()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "My Recordings");
        Directory.CreateDirectory(folder);

        Process.Start(new ProcessStartInfo(folder)
        {
            UseShellExecute = true
        });
    }

    private void Exit()
    {
        pollTimer.Stop();
        AppLog.Info("Exit requested.");
        StopRecording("Application exited");
        notifyIcon.Visible = false;
        Application.Exit();
    }

    private void UpdateMenuState()
    {
        if (activeTarget is null)
        {
            statusItem.Text = activePostProcessingTasks > 0 ? "Processing recording" : "Idle";
            notifyIcon.Text = activePostProcessingTasks > 0 ? "Processing recording" : "Laptop Output Recorder";
            stopRecordingItem.Enabled = false;
        }
        else
        {
            statusItem.Text = $"Recording {activeTarget.DisplayName}";
            notifyIcon.Text = $"Recording {activeTarget.DisplayName}";
            stopRecordingItem.Enabled = true;
        }

        watchedAppsItem.Text = $"Watching: {WatchedAppsSummary()}";
    }

    private string WatchedAppsSummary()
    {
        return string.Join(", ", settings.WatchedApps.Select(app => app.DisplayName));
    }

    private void ShowNotification(string title, string text, ToolTipIcon icon)
    {
        notifyIcon.BalloonTipTitle = title;
        notifyIcon.BalloonTipText = text;
        notifyIcon.BalloonTipIcon = icon;
        notifyIcon.ShowBalloonTip(3000);
    }

    private async Task TranscribeRecordingAsync(RecordingFiles files)
    {
        activePostProcessingTasks++;
        UpdateMenuState();

        try
        {
            ShowNotification("Transcription started", Path.GetFileName(files.ProcessingPath), ToolTipIcon.Info);

            try
            {
                var transcriptPath = await transcriptionService.TranscribeAsync(files.ProcessingPath);
                ShowNotification("Transcription finished", transcriptPath, ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                ShowNotification("Transcription failed", ex.Message, ToolTipIcon.Error);
            }

            await DiarizeRecordingAsync(files);
        }
        catch (Exception ex)
        {
            ShowNotification("Post-processing failed", ex.Message, ToolTipIcon.Error);
        }
        finally
        {
            activePostProcessingTasks--;
            UpdateMenuState();
        }
    }

    private async Task DiarizeRecordingAsync(RecordingFiles files)
    {
        ShowNotification("Diarization started", Path.GetFileName(files.ProcessingPath), ToolTipIcon.Info);

        var result = await diarizationService.DiarizeAsync(files.ProcessingPath);
        if (result.Success)
        {
            var meetingPath = await meetingComposer.ComposeAsync(files.ProcessingPath, result, files);
            if (meetingPath is not null)
            {
                await speakerDatabase.ResolveMeetingAsync(meetingPath);
                AppLog.Info($"Meeting JSON written: {meetingPath}");
            }

            ShowNotification("Diarization finished", meetingPath ?? result.VoiceprintsPath ?? result.TextPath ?? result.JsonPath ?? files.ProcessingPath, ToolTipIcon.Info);
            return;
        }

        if (result.Skipped)
        {
            ShowNotification("Diarization skipped", result.Message, ToolTipIcon.Warning);
            return;
        }

        AppLog.Error($"Diarization failed for {files.ProcessingPath}: {result.Message}");
        ShowNotification("Diarization failed", $"See log: {AppLog.FilePath}", ToolTipIcon.Error);
    }
}
