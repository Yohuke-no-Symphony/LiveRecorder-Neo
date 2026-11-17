namespace Yohuke.LiveRecorder.AI.Contracts;

/// <summary>与通用旧租约恢复证明匹配的 AI 私有检查点和已知模型响应状态。</summary>
public sealed record AiRecoveryCheckpoint
{
    public string JobId { get; set; } = "";
    public int Attempt { get; set; }
    public long Sequence { get; set; }
    public int ModelRequests { get; set; }
    public bool HasKnownResponse { get; set; }
    public bool HasDispatchedRequest { get; set; } = true;
    public string? RequestFingerprint { get; set; }
}
