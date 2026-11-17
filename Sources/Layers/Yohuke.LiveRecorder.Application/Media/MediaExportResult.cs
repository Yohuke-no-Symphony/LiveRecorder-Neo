namespace Yohuke.LiveRecorder.Application.Media;

/// <summary>完成校验的媒体导出结果。</summary>
public sealed class MediaExportResult
{
    public double ActualStartMs { get; set; }
    public double ActualEndMs { get; set; }
    public long Bytes { get; set; }
}
