namespace Yohuke.LiveRecorder.AI.Worker.Storage;

internal sealed record AiRecordedProviderError
{
    public string Code { get; set; } = "";
    public bool UnknownOutcome { get; set; }
    public long? PromptTokens { get; set; }
    public long? CompletionTokens { get; set; }
    public bool Retryable { get; set; }
}
