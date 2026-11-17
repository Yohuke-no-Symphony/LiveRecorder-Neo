using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.AI.Contracts;

/// <summary>带幂等序号的最终分析结果或连接测试结果。</summary>
public sealed record AiExecutionResult
{
    public long Sequence { get; set; }
    public AiExecutionState Execution { get; set; } = new();
    public AiAnalysisMemory? Memory { get; set; }
    public AiConnectionTestOutcome? ConnectionTest { get; set; }
}
