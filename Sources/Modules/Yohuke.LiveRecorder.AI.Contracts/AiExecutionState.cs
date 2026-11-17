using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.AI.Contracts;

/// <summary>在节点和主站检查点之间传输的 AI 执行状态。</summary>
public sealed record AiExecutionState
{
    public string JobId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string Purpose { get; set; } = AiJobPurposes.ANALYSIS;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public AiExecutionSettings Settings { get; set; } = new();
    public AiAnalysisSnapshot Snapshot { get; set; } = new();
    public AiAnalysisResult Result { get; set; } = new();
    public AiAnalysisContextState Context { get; set; } = new();
    public int TokenBudget { get; set; }
    public string State { get; set; } = "Running";
    public string? ErrorCode { get; set; }
    public double Progress { get; set; }
    public long PromptTokens { get; set; }
    public long CompletionTokens { get; set; }
    public long EstimatedTokens { get; set; }
    public long ReservedTokens { get; set; }
    public int ModelRequests { get; set; }
    public int RequestRetries { get; set; }
    public int ToolCalls { get; set; }
    public bool RequestInFlight { get; set; }
}
