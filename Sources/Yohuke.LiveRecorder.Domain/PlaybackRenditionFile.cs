namespace Yohuke.LiveRecorder.Domain;

/// <summary>可按单个播放文件查询的已验证元数据，不受整份清单文档大小限制。</summary>
public sealed class PlaybackRenditionFile : Entity
{
    public string AssetId { get; set; } = "";
    public string RenditionId { get; set; } = "";
    public string Name { get; set; } = "";
    public long Length { get; set; }
    public string Sha256 { get; set; } = "";
    public long WriteTicks { get; set; }
}
