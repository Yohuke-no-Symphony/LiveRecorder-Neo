using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Worker.Common.Storage;

internal sealed class WorkerJobCheckpoint<TAssignment, TPayload>
    where TAssignment : new()
    where TPayload : new()
{
    public int SchemaVersion { get; set; } = 1;
    public int PipelineVersion { get; set; } = 1;
    public string WorkerId { get; set; } = "";
    public string JobId { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public TAssignment Assignment { get; set; } = new();
    public TPayload Payload { get; set; } = new();
    public List<WorkerStageSnapshot> Stages { get; set; } = [];
    public string State { get; set; } = "Running";
    public string? NextLeaseToken { get; set; }
    public WorkerJobResult? PendingResult { get; set; }
    public DateTime LastAccessAtUtc { get; set; } = DateTime.UtcNow;
}
