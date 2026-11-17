namespace Yohuke.LiveRecorder.Infrastructure.Playback;

/// <summary>保存已发布窗口文件的内容身份，避免每次播放重新读取媒体计算摘要。</summary>
public sealed record PlaybackWindowFileMetadata(string Name, long Length, long WriteTicks, string ContentHash);
