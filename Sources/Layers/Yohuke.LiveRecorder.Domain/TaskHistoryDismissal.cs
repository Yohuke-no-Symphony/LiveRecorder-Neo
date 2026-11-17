namespace Yohuke.LiveRecorder.Domain;

/// <summary>只隐藏指定视图中的一次已结束任务，不参与执行、发布或媒体引用。</summary>
public sealed class TaskHistoryDismissal : Entity
{
    public string Scope { get; set; } = "";
    public string ViewerId { get; set; } = "";
    public string Kind { get; set; } = "";
    public string TaskId { get; set; } = "";
    public int Attempt { get; set; }
    public DateTime FinishedAtUtc { get; set; }
}
