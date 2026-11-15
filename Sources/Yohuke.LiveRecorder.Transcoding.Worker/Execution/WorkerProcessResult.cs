namespace Yohuke.LiveRecorder.Transcoding.Worker.Execution;

internal sealed record WorkerProcessResult(int ExitCode, string Output, string Diagnostic);
