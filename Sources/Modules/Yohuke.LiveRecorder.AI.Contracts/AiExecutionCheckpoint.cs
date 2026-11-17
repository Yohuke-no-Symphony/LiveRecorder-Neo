using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.AI.Contracts;

/// <summary>带幂等序号的 AI 执行和私有上下文检查点。</summary>
public sealed record AiExecutionCheckpoint
{
    public long Sequence { get; set; }
    public AiExecutionState Execution { get; set; } = new();
    public AiAnalysisMemory? Memory { get; set; }
}
