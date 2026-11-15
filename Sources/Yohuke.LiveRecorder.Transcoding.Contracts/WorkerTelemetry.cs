namespace Yohuke.LiveRecorder.Transcoding.Contracts;

/// <summary>Worker 实际资源使用与补位准入信息。</summary>
public sealed record WorkerTelemetry
{
    public double? CpuPercent { get; set; }
    public int CpuThreadBudget { get; set; }
    public int RunningGpu { get; set; }
    public int RunningCpu { get; set; }
    public long FreeBytes { get; set; }
    public string? CpuUnavailableReason { get; set; }
}
