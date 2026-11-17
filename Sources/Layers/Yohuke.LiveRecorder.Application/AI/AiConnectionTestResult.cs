namespace Yohuke.LiveRecorder.Application.AI;

/// <summary>连接与函数调用能力测试的最小结果。</summary>
public sealed record AiConnectionTestResult(bool Success, long? PromptTokens, long? CompletionTokens);
