using Yohuke.LiveRecorder.AI.Contracts;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.AI.Worker.Storage;

internal sealed class AiResponseJournal
{
    public int RequestNumber { get; set; }
    public string? RequestFingerprint { get; set; }
    public bool Dispatched { get; set; }
    public bool Confirmed { get; set; }
    public bool Returned { get; set; }
    public bool Applied { get; set; }
    public AiExecutionState BeforeExecution { get; set; } = new();
    public AiAnalysisMemory BeforeMemory { get; set; } = new();
    public AiCompletion? Completion { get; set; }
    public AiRecordedProviderError? Error { get; set; }
    public bool HasKnownResponse => Completion is not null || Error is { UnknownOutcome: false };
}
