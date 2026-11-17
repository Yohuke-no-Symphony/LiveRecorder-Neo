namespace Yohuke.LiveRecorder.AI.Contracts;

/// <summary>主站对同一执行序号和状态内容的持久确认。</summary>
public sealed record AiCheckpointAcknowledgement
{
    public long Sequence { get; set; }
    public string State { get; set; } = "";
}
