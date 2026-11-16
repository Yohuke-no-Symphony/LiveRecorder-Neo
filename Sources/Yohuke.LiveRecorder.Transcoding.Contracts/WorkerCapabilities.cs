namespace Yohuke.LiveRecorder.Transcoding.Contracts;

/// <summary>Worker 工具与设备能力快照。</summary>
public sealed record WorkerCapabilities
{
    public string OperatingSystem { get; set; } = "";
    public string Architecture { get; set; } = "";
    public string ToolVersion { get; set; } = "";
    public int HlsPackagingVersion { get; set; }
    public int ClippingVersion { get; set; }
    public List<EncoderCapability> Encoders { get; set; } = [];
}
