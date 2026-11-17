namespace Yohuke.LiveRecorder.AI.Contracts;

/// <summary>节点检查模型连接及工具调用能力后返回的最小结果。</summary>
public sealed record AiConnectionTestOutcome
{
    public bool Success { get; set; }
    public long? PromptTokens { get; set; }
    public long? CompletionTokens { get; set; }
}
