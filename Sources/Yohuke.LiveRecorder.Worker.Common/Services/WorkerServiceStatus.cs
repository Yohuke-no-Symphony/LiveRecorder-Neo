namespace Yohuke.LiveRecorder.Transcoding.Worker.Services;

internal sealed record WorkerServiceStatus
{
    internal bool Installed { get; init; }
    internal bool IsRunning { get; init; }
    internal string State { get; init; } = "Not installed";
    internal string Startup { get; init; } = "Unavailable";
    internal int? ProcessId { get; init; }
    internal string LogSource { get; init; } = "";
}
