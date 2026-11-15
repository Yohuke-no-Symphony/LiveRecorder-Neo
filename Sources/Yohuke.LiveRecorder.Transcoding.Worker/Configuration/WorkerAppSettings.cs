namespace Yohuke.LiveRecorder.Transcoding.Worker.Configuration;

internal sealed class WorkerAppSettings
{
    public WorkerConnectionOptions Worker { get; set; } = new();
}
