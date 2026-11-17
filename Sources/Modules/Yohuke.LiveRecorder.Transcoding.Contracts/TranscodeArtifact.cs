namespace Yohuke.LiveRecorder.Transcoding.Contracts;

/// <summary>单个成品文件的长度与内容校验值。</summary>
public sealed record TranscodeArtifact
{
    public string Name { get; set; } = "";
    public long Length { get; set; }
    public string Sha256 { get; set; } = "";
}
