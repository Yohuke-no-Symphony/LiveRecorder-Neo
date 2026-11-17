namespace Yohuke.LiveRecorder.Transcoding.Contracts;

/// <summary>以持久的旧租约证明恢复同一执行代次，并轮换写入凭据。</summary>
public sealed record WorkerJobRecoveryRequest
{
    public string JobId { get; set; } = "";
    public int Attempt { get; set; }
    public string LeaseToken { get; set; } = "";
    public string NextLeaseToken { get; set; } = "";
}
