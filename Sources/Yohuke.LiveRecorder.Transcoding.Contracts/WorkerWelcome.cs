namespace Yohuke.LiveRecorder.Transcoding.Contracts;

/// <summary>主站确认的运行配置。</summary>
public sealed record WorkerWelcome
{
    public WorkerConfiguration Configuration { get; set; } = new();
    public int ConfigurationRevision { get; set; }
    public int HeartbeatSeconds { get; set; } = 2;
}
