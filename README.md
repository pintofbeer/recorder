# Laptop Output Recorder

A Windows system tray app that records laptop audio output when configured meeting apps are active.

## Behavior

- Starts recording when a watched app comes into focus.
- Keeps recording if focus moves elsewhere.
- Stops when the app window is closed or minimized.
- Saves WAV files to `%USERPROFILE%\My Recordings`.
- Transcribes completed recordings to matching `.txt` files.
- Runs speaker diarization on completed recordings when pyannote is configured.
- Watches Zoom and Microsoft Teams by default.

## Configure Watched Apps

The app creates this file on first launch:

```text
%APPDATA%\LaptopOutputRecorder\settings.json
```

Default contents:

```json
{
  "watchedApps": [
    {
      "displayName": "Zoom",
      "processNames": [ "Zoom" ],
      "captureMicrophone": true
    },
    {
      "displayName": "Microsoft Teams",
      "processNames": [ "Teams", "MSTeams", "ms-teams" ],
      "captureMicrophone": true
    }
  ],
  "diarization": {
    "enabled": true,
    "pythonExecutable": "python",
    "huggingFaceToken": "",
    "model": "pyannote/speaker-diarization-community-1",
    "embeddingModel": "pyannote/embedding",
    "device": "auto"
  }
}
```

Add more apps by adding their executable process names without `.exe`. Use the tray menu's **Edit watched apps** and **Reload watched apps** items after the app is running.

`captureMicrophone` is per watched app. Zoom and Microsoft Teams default to `true`. Other apps, such as Chrome, should usually stay `false` unless you explicitly want local microphone capture for that app.

When microphone capture is enabled, the app writes separate and merged audio files:

```text
Microsoft Teams-20260603-090000.wav
Microsoft Teams-20260603-090000.mic.wav
Microsoft Teams-20260603-090000.merged.wav
```

Transcription, diarization, voiceprints, and meeting JSON use the merged file. The original output and microphone tracks are referenced in `meeting.json`.

## Transcription

Completed recordings are transcribed locally with Whisper.net. The transcript is saved next to the recording:

```text
%USERPROFILE%\My Recordings\Microsoft Teams-20260602-201500.wav
%USERPROFILE%\My Recordings\Microsoft Teams-20260602-201500.txt
```

The first transcription downloads and caches the Whisper base GGML model here:

```text
%APPDATA%\LaptopOutputRecorder\Models\ggml-base.bin
```

That first run needs internet access and can take a little while. Later transcripts reuse the cached model.

## Speaker Diarization

Completed recordings are diarized with pyannote after transcription finishes. If transcription fails, diarization still runs over the audio file.

The app writes these files next to the recording:

```text
%USERPROFILE%\My Recordings\Microsoft Teams-20260602-201500.diarization.json
%USERPROFILE%\My Recordings\Microsoft Teams-20260602-201500.rttm
%USERPROFILE%\My Recordings\Microsoft Teams-20260602-201500.speakers.txt
%USERPROFILE%\My Recordings\Microsoft Teams-20260602-201500.voiceprints.json
%USERPROFILE%\My Recordings\Microsoft Teams-20260602-201500.meeting.json
```

`voiceprints.json` contains one duration-weighted, normalized speaker embedding per diarized speaker. These are useful for later speaker matching or assigning stable names to recurring speakers.

`meeting.json` is the canonical combined format. It includes:

- meeting metadata with an inferred meeting name and observed foreground window titles
- top-level speaker entries with embeddings and placeholder fields for later database resolution
- transcript segments with `speakerId`, `speakerConfidence`, and placeholder resolved speaker fields
- raw diarization turns for downstream reprocessing

For Zoom and Microsoft Teams recordings, the app tracks foreground window title changes while recording. The meeting file stores those raw observations under `meeting.windowTitles` and writes the best conservative guess to `meeting.name` when a useful title can be inferred.

## Speaker Database

Known speakers are stored locally in SQLite:

```text
%APPDATA%\LaptopOutputRecorder\speakers.db
```

The app stores multiple embeddings per known speaker and uses cosine similarity to match new meeting speakers. At this scale, matching is a linear scan and does not need Postgres or a vector index.

Settings:

```json
"speakerMatching": {
  "enabled": true,
  "threshold": 0.85,
  "ambiguousMargin": 0.03
}
```

Matching updates `meeting.json` with:

- `resolvedSpeakerId`
- `name`
- `speakerMatchConfidence`
- `speakerMatchStatus`

Use the tray menu's **Tag speakers...** item to select a `.meeting.json`, assign diarized speaker IDs to existing or new people, store their embeddings, and update the meeting file. Future meetings are matched automatically after diarization.

The tagging window includes a **Play** button for each diarized speaker. It previews the longest voiceprint segment for that speaker from the original recording, which is usually the most useful sample for manual identification.

The pyannote integration runs in an isolated Python worker process rather than inside the WinForms process. This is more robust for PyTorch/pyannote native dependencies than embedding them directly with CSnakes.

Prerequisites:

- Python installed on Windows.
- `ffmpeg` available on `PATH`.
- Hugging Face access to the pyannote model.
- A Hugging Face token set as `HF_TOKEN`, or stored in `diarization.huggingFaceToken` in `%APPDATA%\LaptopOutputRecorder\settings.json`.

Set up the Python environment:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\Setup-Pyannote.ps1
```

The setup script creates this virtual environment:

```text
%APPDATA%\LaptopOutputRecorder\pyannote-venv
```

The recorder auto-detects that environment. To use a different environment, set `diarization.pythonExecutable` to the full path of its `python.exe`.

GPU usage:

- `diarization.device` defaults to `auto`.
- `auto` uses CUDA when `torch.cuda.is_available()` is true, otherwise CPU.
- Set `diarization.device` to `cuda` to require the Nvidia GPU and fail fast if CUDA is unavailable.
- Set it to `cpu` to force CPU.

The default pip-installed PyTorch package may be CPU-only. If `recorder.log` says `pyannote device: cpu`, install a CUDA-enabled PyTorch build in `%APPDATA%\LaptopOutputRecorder\pyannote-venv`.

Pyannote, Hugging Face, Torch, and matplotlib cache files are kept under:

```text
%APPDATA%\LaptopOutputRecorder\Cache
```

## Build

```powershell
dotnet build
```

## Icon

The executable and tray icon use `Resources\AppIcon.ico`. Regenerate it with:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\Generate-AppIcon.ps1
```

## Run

```powershell
dotnet run
```

Right-click the tray icon to open recordings, edit settings, reload settings, manually stop a recording, or exit.

Use the tray menu's **Launch at startup** item to start the recorder automatically when you sign in. This uses the current user's Windows startup registry entry and does not require administrator rights. A Windows Service is not used because the recorder needs to run in the interactive desktop session to show the tray icon and monitor foreground windows.

## Troubleshooting

If the app starts but you do not see it, check the Windows tray overflow menu first. Windows may hide new tray icons behind the `^` button until you pin them.

Startup and runtime diagnostics are written here when available:

```text
%APPDATA%\LaptopOutputRecorder\recorder.log
```

If diarization reports a Python error, the tray notification is intentionally short; the full pyannote stdout/stderr is written to this log.
