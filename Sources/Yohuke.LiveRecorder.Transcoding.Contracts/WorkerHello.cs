namespace Yohuke.LiveRecorder.Transcoding.Contracts;

/// <summary>TLS 建立后的 Worker 身份与初始配置交换。</summary>
public sealed record WorkerHello
{
    public string WorkerId { get; set; } = "";
    public string Kind { get; set; } = "Transcoding";
    public string Secret { get; set; } = "";
    public WorkerCapabilities Capabilities { get; set; } = new();
    public WorkerConfiguration? InitialConfiguration { get; set; }
    public int? ExpectedConfigurationRevision { get; set; }
}
