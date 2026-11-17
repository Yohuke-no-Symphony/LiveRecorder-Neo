namespace Yohuke.LiveRecorder.Domain;

/// <summary>具有独立租约、参考用量和增量结果的持久 AI 作业。</summary>
public sealed class AiAnalysisJob : Entity
{
    public string SessionId { get; set; } = "";
    public string OwnerId { get; set; } = "";
    public string OwnerName { get; set; } = "";
    public TaskProvenance Provenance { get; set; } = new();
    public string Purpose { get; set; } = AiJobPurposes.ANALYSIS;
    public string? LiveMarkerFileId { get; set; }
    public bool PublicationStarted { get; set; }
    public string State { get; set; } = "Queued";
    public string? ActiveKey { get; set; }
    public string Identity { get; set; } = "";
    public string CredentialFingerprint { get; set; } = "";
    public AiSettings Settings { get; set; } = new();
    public AiAnalysisSnapshot Snapshot { get; set; } = new();
    public AiAnalysisResult Result { get; set; } = new();
    public AiAnalysisContextState Context { get; set; } = new();
    public int TokenBudget { get; set; }
    public long PromptTokens { get; set; }
    public long CompletionTokens { get; set; }
    public long EstimatedTokens { get; set; }
    public int ModelRequests { get; set; }
    public int RequestRetries { get; set; }
    public int ToolCalls { get; set; }
    public long ReservedTokens { get; set; }
    public bool RequestInFlight { get; set; }
    public bool CancelRequested { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTime? LeaseUntilUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
    public string? ErrorCode { get; set; }
    public double Progress { get; set; }
}
