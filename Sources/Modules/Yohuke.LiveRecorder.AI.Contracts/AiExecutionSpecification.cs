using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.AI.Contracts;

/// <summary>通过有效租约临时领取的执行参数、输入描述和模型凭据。</summary>
public sealed record AiExecutionSpecification
{
    public long CheckpointSequence { get; set; }
    public AiExecutionState Execution { get; set; } = new();
    public AiInputManifest Input { get; set; } = new();
    public AiAnalysisMemory? Memory { get; set; }
    public string ApiKey { get; set; } = "";
    public bool IsConnectionTest { get; set; }
}
