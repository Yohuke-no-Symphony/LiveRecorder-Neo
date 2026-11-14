namespace Yohuke.LiveRecorder.AI;

/// <summary>不包含响应正文或凭据的安全供应商错误。</summary>
public sealed class AiProviderException(string code, bool unknownOutcome = false, long? promptTokens = null,
    long? completionTokens = null, bool retryable = false) : Exception(code)
{
    public string Code { get; } = code;
    public bool UnknownOutcome { get; } = unknownOutcome;
    public long? PromptTokens { get; } = promptTokens;
    public long? CompletionTokens { get; } = completionTokens;
    public bool Retryable { get; } = retryable;
}
