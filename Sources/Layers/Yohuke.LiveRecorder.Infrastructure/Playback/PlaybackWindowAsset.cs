namespace Yohuke.LiveRecorder.Infrastructure.Playback;

/// <summary>已验证的不可变播放窗口文件与浏览器缓存内容摘要。</summary>
public sealed record PlaybackWindowAsset(string Path, string ContentHash);
