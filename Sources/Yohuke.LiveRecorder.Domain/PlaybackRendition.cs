namespace Yohuke.LiveRecorder.Domain;

/// <summary>按源版本和编码配方发布的可共享播放产物。</summary>
public sealed class PlaybackRendition : Entity
{
    public string AssetId { get; set; } = "";
    public string SourceVersion { get; set; } = "";
    public string ProfileId { get; set; } = "";
    public string ProfileName { get; set; } = "";
    public string RecipeHash { get; set; } = "";
    public string RelativeDirectory { get; set; } = "";
    public string State { get; set; } = "Ready";
    public string JobId { get; set; } = "";
    public int Attempt { get; set; }
    public double DurationMs { get; set; }
    public double SourceStartMs { get; set; }
    public double? PlaybackTimestampOffsetMs { get; set; }
    public double? MediaEndTimestampMs { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double FrameRate { get; set; }
    public long Bytes { get; set; }
    public List<RenditionArtifact> Artifacts { get; set; } = [];
}
