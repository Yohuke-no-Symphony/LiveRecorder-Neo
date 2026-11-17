namespace Yohuke.LiveRecorder.Transcoding.Contracts;

/// <summary>经实际短样本编码验证的设备后端。</summary>
public sealed record EncoderCapability
{
    public string Encoder { get; set; } = "";
    public string? DeviceId { get; set; }
    public bool Available { get; set; }
    public string? Error { get; set; }
}
