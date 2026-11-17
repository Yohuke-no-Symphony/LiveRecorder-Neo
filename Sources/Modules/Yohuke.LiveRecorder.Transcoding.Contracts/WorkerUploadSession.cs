namespace Yohuke.LiveRecorder.Transcoding.Contracts;

/// <summary>将一个执行代次的上传断点绑定到不可变成品内容。</summary>
public sealed record WorkerUploadSession
{
    public string ManifestFingerprint { get; set; } = "";
}
