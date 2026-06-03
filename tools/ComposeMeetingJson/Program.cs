using Recorder;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: ComposeMeetingJson <audio-path>");
    Environment.ExitCode = 2;
    return;
}

var audioPath = args[0];
var diarization = DiarizationResult.Completed(
    Path.ChangeExtension(audioPath, ".diarization.json"),
    Path.ChangeExtension(audioPath, ".rttm"),
    Path.ChangeExtension(audioPath, ".speakers.txt"),
    Path.ChangeExtension(audioPath, ".voiceprints.json"));

var output = await new MeetingComposer().ComposeAsync(audioPath, diarization);
Console.WriteLine(output ?? "No meeting JSON written.");
