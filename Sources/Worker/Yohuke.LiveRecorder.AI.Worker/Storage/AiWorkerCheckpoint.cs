using Yohuke.LiveRecorder.AI.Contracts;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.AI.Worker.Storage;

internal sealed class AiWorkerCheckpoint
{
    public AiAssignment Assignment { get; set; } = new();
    public AiInputManifest Input { get; set; } = new();
    public AiExecutionState Execution { get; set; } = new();
    public AiAnalysisMemory Memory { get; set; } = new();
    public AiExecutionCheckpoint? Pending { get; set; }
    public AiExecutionResult? PendingResult { get; set; }
    public AiResponseJournal? Journal { get; set; }
    public long Sequence { get; set; }
    public string? NextLeaseToken { get; set; }
    public string State { get; set; } = "Working";
    public bool InputReady { get; set; }
    public List<AiExecutionStage> Stages { get; set; } = [new() { Id = "Downloading" }, new() { Id = "Preparing" }, new() { Id = "Analyzing" }, new() { Id = "Reporting" }];
    public DateTime LastAccessAtUtc { get; set; } = DateTime.UtcNow;
}
