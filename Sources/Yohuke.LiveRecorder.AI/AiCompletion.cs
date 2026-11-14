using System.Text.Json.Nodes;

namespace Yohuke.LiveRecorder.AI;

/// <summary>保留工具调用与供应商使用量的单轮响应。</summary>
public sealed class AiCompletion
{
    public JsonObject Message { get; init; } = new();
    public long? PromptTokens { get; init; }
    public long? CompletionTokens { get; init; }
    public string FinishReason { get; init; } = "";
}
