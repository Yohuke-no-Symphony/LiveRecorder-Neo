namespace Yohuke.LiveRecorder.Application.Media;

/// <summary>保留所有编码区间的修复结果。</summary>
public sealed class MediaRepairResult
{
    public List<MediaRepairOutput> Outputs { get; set; } = [];
}
