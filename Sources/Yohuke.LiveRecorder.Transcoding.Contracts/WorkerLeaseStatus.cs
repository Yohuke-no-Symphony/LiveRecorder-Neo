namespace Yohuke.LiveRecorder.Transcoding.Contracts;

/// <summary>主站对执行租约的续期或撤销结果。</summary>
public sealed record WorkerLeaseStatus
{
    public string JobId { get; set; } = "";
    public int Attempt { get; set; }
    public bool Accepted { get; set; }
    public int LeaseSeconds { get; set; } = 90;
}
