namespace Yohuke.LiveRecorder.Infrastructure.Playback;

/// <summary>同一时间桶和事件种类内的记录摘要，时间指向首条记录。</summary>
public sealed class TimelineMarker
{
    public double OffsetMs { get; set; }
    public double EndOffsetMs { get; set; }
    public string Kind { get; set; } = "";
    public int Count { get; set; }
    public long Sequence { get; set; }
    public string UserName { get; set; } = "";
    public string Text { get; set; } = "";
    public decimal? Amount { get; set; }
    public long Quantity { get; set; }
}
