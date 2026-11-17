namespace Yohuke.LiveRecorder.Infrastructure.Playback;

internal sealed class TimelineMarkerState
{
    public bool Included { get; set; }
    public bool Baseline { get; set; }
    public string Kind { get; set; } = "";
    public double OffsetMs { get; set; }
    public double EndOffsetMs { get; set; }
    public long Sequence { get; set; }
    public string UserName { get; set; } = "";
    public string Text { get; set; } = "";
    public long OriginalQuantity { get; set; }
    public long Quantity { get; set; }
    public decimal? UnitAmount { get; set; }
    public decimal? Amount { get; set; }
    public HashSet<string> Rows { get; set; } = new(StringComparer.Ordinal);
}
