using System.Text.Json;

namespace Recorder;

public sealed class SpeakerTaggingForm : Form
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly SpeakerDatabase speakerDatabase;
    private readonly string meetingPath;
    private readonly List<TagRow> rows = [];
    private readonly AudioPreviewPlayer previewPlayer = new();
    private readonly Button saveButton = new() { Text = "Save", AutoSize = true };
    private readonly Button autoTagButton = new() { Text = "Auto tag", AutoSize = true };

    private MeetingDocument? meeting;

    public SpeakerTaggingForm(SpeakerDatabase speakerDatabase, string meetingPath)
    {
        this.speakerDatabase = speakerDatabase;
        this.meetingPath = meetingPath;

        Text = "Tag Speakers";
        Width = 820;
        Height = 420;
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        MaximizeBox = false;

        autoTagButton.Click += async (_, _) => await AutoTagAsync();
        saveButton.Click += async (_, _) => await SaveAsync();
        BuildLayout();
    }

    private void BuildLayout()
    {
        rows.Clear();
        meeting = ReadMeeting();
        var knownSpeakers = speakerDatabase.GetSpeakers();
        var speakers = meeting.Speakers
            .OrderBy(speaker => speaker.Id)
            .ToList();

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 3
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        root.Controls.Add(new Label
        {
            AutoSize = true,
            Text = $"Meeting: {Path.GetFileName(meetingPath)}"
        }, 0, 0);

        var grid = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            ColumnCount = 7,
            RowCount = 1,
            Padding = new Padding(0, 12, 0, 12)
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));

        AddHeader(grid, 0, "Speaker");
        AddHeader(grid, 1, "Play");
        AddHeader(grid, 2, "Duration");
        AddHeader(grid, 3, "Existing");
        AddHeader(grid, 4, "New name");
        AddHeader(grid, 5, "Enroll");
        AddHeader(grid, 6, "Status");

        var rowIndex = 1;
        foreach (var speaker in speakers)
        {
            var existing = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Dock = DockStyle.Fill
            };
            existing.Items.Add("");
            foreach (var knownSpeaker in knownSpeakers)
            {
                existing.Items.Add(knownSpeaker.DisplayName);
            }

            existing.SelectedItem = speaker.Name is not null && existing.Items.Contains(speaker.Name)
                ? speaker.Name
                : "";

            var newName = new TextBox
            {
                Dock = DockStyle.Fill,
                Text = speaker.Name is not null && !existing.Items.Contains(speaker.Name) ? speaker.Name : ""
            };

            var enroll = CreateEnrollCheckBox(speaker);
            var enrollCell = TopControl(enroll);

            var preview = CreatePreviewButton(speaker);

            grid.RowCount = rowIndex + 1;
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            grid.Controls.Add(TopControl(new Label { Text = speaker.Id, AutoSize = true }), 0, rowIndex);
            grid.Controls.Add(TopControl(preview), 1, rowIndex);
            grid.Controls.Add(TopControl(new Label { Text = speaker.VoiceprintDuration?.ToString("0.0s") ?? "", AutoSize = true }), 2, rowIndex);
            grid.Controls.Add(existing, 3, rowIndex);
            grid.Controls.Add(newName, 4, rowIndex);
            grid.Controls.Add(enrollCell, 5, rowIndex);
            grid.Controls.Add(TopControl(new Label
            {
                Text = speaker.Embedding is { Count: > 0 } ? "" : "No voiceprint",
                AutoSize = true
            }), 6, rowIndex);
            rows.Add(new TagRow(speaker.Id, existing, newName, enroll));
            rowIndex++;
        }

        var scrollPanel = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true
        };
        scrollPanel.Controls.Add(grid);
        root.Controls.Add(scrollPanel, 0, 1);

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.RightToLeft
        };
        var cancelButton = new Button { Text = "Cancel", AutoSize = true };
        cancelButton.Click += (_, _) => Close();
        buttons.Controls.Add(saveButton);
        buttons.Controls.Add(cancelButton);
        buttons.Controls.Add(autoTagButton);
        root.Controls.Add(buttons, 0, 2);

        Controls.Add(root);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            previewPlayer.Dispose();
        }

        base.Dispose(disposing);
    }

    private Button CreatePreviewButton(MeetingSpeaker speaker)
    {
        var button = new Button
        {
            Text = "Play",
            Width = 48,
            Height = 26,
            Enabled = TryGetPreviewRange(speaker, out _, out _)
        };

        button.Click += (_, _) =>
        {
            if (!TryGetPreviewRange(speaker, out var start, out var end))
            {
                return;
            }

            try
            {
                previewPlayer.Play(meeting!.Audio, start, end);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Playback failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };

        return button;
    }

    private static CheckBox CreateEnrollCheckBox(MeetingSpeaker speaker)
    {
        return new CheckBox
        {
            Checked = speaker.Embedding is { Count: > 0 },
            Enabled = speaker.Embedding is { Count: > 0 },
            AutoSize = true,
            Margin = Padding.Empty
        };
    }

    private static Panel TopControl(Control control)
    {
        var panel = new Panel { Dock = DockStyle.Fill };
        control.Anchor = AnchorStyles.Top;
        panel.Controls.Add(control);
        panel.Resize += (_, _) =>
        {
            control.Left = Math.Max(0, (panel.ClientSize.Width - control.Width) / 2);
            control.Top = 3;
        };
        return panel;
    }

    private bool TryGetPreviewRange(MeetingSpeaker speaker, out double start, out double end)
    {
        var segment = speaker.VoiceprintSegments?
            .Where(segment => segment.End > segment.Start)
            .OrderByDescending(segment => segment.Duration)
            .FirstOrDefault();

        if (segment is null && meeting is not null)
        {
            var diarizationSegment = meeting.Diarization
                .Where(segment => segment.Speaker == speaker.Id && segment.End > segment.Start)
                .OrderByDescending(segment => segment.End - segment.Start)
                .FirstOrDefault();

            if (diarizationSegment is not null)
            {
                start = Math.Max(0, diarizationSegment.Start - 0.15);
                end = diarizationSegment.End + 0.15;
                return true;
            }
        }

        if (segment is null)
        {
            start = 0;
            end = 0;
            return false;
        }

        start = Math.Max(0, segment.Start - 0.15);
        end = segment.End + 0.15;
        return true;
    }

    private static void AddHeader(TableLayoutPanel grid, int column, string text)
    {
        grid.Controls.Add(new Label
        {
            Text = text,
            AutoSize = true,
            Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold)
        }, column, 0);
    }

    private MeetingDocument ReadMeeting()
    {
        using var stream = File.OpenRead(meetingPath);
        return JsonSerializer.Deserialize<MeetingDocument>(stream, JsonOptions)
            ?? throw new InvalidOperationException($"Could not read {meetingPath}.");
    }

    private async Task AutoTagAsync()
    {
        saveButton.Enabled = false;
        autoTagButton.Enabled = false;

        try
        {
            await speakerDatabase.ResolveMeetingAsync(meetingPath);
            Controls.Clear();
            BuildLayout();
            MessageBox.Show(this, "Auto tagging complete.", "Tag Speakers", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Auto tagging failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            saveButton.Enabled = true;
            autoTagButton.Enabled = true;
        }
    }

    private async Task SaveAsync()
    {
        saveButton.Enabled = false;
        autoTagButton.Enabled = false;

        try
        {
            foreach (var row in rows.Where(row => row.Enroll.Checked))
            {
                var name = !string.IsNullOrWhiteSpace(row.NewName.Text)
                    ? row.NewName.Text.Trim()
                    : row.ExistingSpeaker.SelectedItem?.ToString()?.Trim();

                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                await speakerDatabase.EnrollSpeakerFromMeetingAsync(meetingPath, row.MeetingSpeakerId, name);
            }

            await speakerDatabase.ResolveMeetingAsync(meetingPath);
            DialogResult = DialogResult.OK;
            Close();
        }
        finally
        {
            saveButton.Enabled = true;
            autoTagButton.Enabled = true;
        }
    }

    private sealed record TagRow(
        string MeetingSpeakerId,
        ComboBox ExistingSpeaker,
        TextBox NewName,
        CheckBox Enroll);
}
