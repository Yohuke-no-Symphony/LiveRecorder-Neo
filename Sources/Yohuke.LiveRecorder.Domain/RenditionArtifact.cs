namespace Yohuke.LiveRecorder.Domain;

/// <summary>完成清单中单个可验证播放文件的内容身份。</summary>
public sealed class RenditionArtifact
{
    public string Name { get; set; } = "";
    public long Length { get; set; }
    public string Sha256 { get; set; } = "";
    public long WriteTicks { get; set; }
}
