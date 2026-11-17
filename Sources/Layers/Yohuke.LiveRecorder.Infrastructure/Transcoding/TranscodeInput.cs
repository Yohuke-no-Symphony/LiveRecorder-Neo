namespace Yohuke.LiveRecorder.Infrastructure.Transcoding;

/// <summary>经过有效任务租约授权的源文件范围。</summary>
public sealed record TranscodeInput(string Path, string ETag, long Length);
