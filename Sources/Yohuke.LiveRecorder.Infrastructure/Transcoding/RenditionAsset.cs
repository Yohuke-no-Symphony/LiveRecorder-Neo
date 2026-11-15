namespace Yohuke.LiveRecorder.Infrastructure.Transcoding;

/// <summary>经过当前场次权限和物理版本校验的派生播放文件。</summary>
public sealed record RenditionAsset(string Path, string ContentHash, long Length);
