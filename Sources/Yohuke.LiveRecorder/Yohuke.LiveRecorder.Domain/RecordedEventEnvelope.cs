using System.Text.Json.Nodes;

namespace Yohuke.LiveRecorder.Domain;

/// <summary>可独立重放和重新分析的文件事件。</summary>
public sealed class RecordedEventEnvelope
{
    public int SchemaVersion { get; set; } = 1;
    public long Sequence { get; set; }
    public string EventId { get; set; } = "";
    public string Kind { get; set; } = "Danmu";
    public string Command { get; set; } = "";
    public DateTime ReceivedAtUtc { get; set; }
    public DateTime? SourceTimeUtc { get; set; }
    public double OffsetMs { get; set; }
    public string UserId { get; set; } = "";
    public string UserName { get; set; } = "";
    public string Text { get; set; } = "";
    public string? TextCode { get; set; }
    public List<string> TextArguments { get; set; } = [];
    public string? AvatarUrl { get; set; }
    public decimal? Amount { get; set; }
    public int Quantity { get; set; } = 1;
    public string? TransactionId { get; set; }
    public string? CoinType { get; set; }
    public string? PriceSource { get; set; }
    public JsonObject? Payload { get; set; }
    public string RawJson { get; set; } = "";
}
