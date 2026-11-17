namespace Yohuke.LiveRecorder.Transcoding.Contracts;

/// <summary>主站对恢复请求的幂等确认；只有 Resume 授予继续执行的权限。</summary>
public sealed record WorkerJobRecoveryStatus
{
    public string JobId { get; set; } = "";
    public int Attempt { get; set; }
    public string Disposition { get; set; } = "Rejected";
    public string State { get; set; } = "";
    public int LeaseSeconds { get; set; } = 90;
}
