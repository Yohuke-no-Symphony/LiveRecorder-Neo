namespace Yohuke.LiveRecorder.Transcoding.Contracts;

/// <summary>完整成品的发布请求，主站必须重新验证所有内容。</summary>
public sealed record TranscodeOutputManifest
{
    public List<TranscodeArtifact> Files { get; set; } = [];
    public TranscodeMediaInfo Media { get; set; } = new();
    public TranscodeMediaInfo Source { get; set; } = new();
    public string Encoder { get; set; } = "";
}
