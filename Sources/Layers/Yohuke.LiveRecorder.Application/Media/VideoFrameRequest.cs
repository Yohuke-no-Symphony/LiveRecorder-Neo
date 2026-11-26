namespace Yohuke.LiveRecorder.Application.Media;

/// <summary>限定封面抽帧使用的录像、索引与已提交字节边界。</summary>
public sealed class VideoFrameRequest
{
    public string SourcePath { get; set; } = "";
    public string IndexPath { get; set; } = "";

    /// <summary>正数限制读取范围。已结束且无索引的完整媒体可使用默认值，由抽帧工具直接定位文件。</summary>
    public long CommittedBytes { get; set; }
    public bool IsClosed { get; set; }
    public string OutputPath { get; set; } = "";
}
