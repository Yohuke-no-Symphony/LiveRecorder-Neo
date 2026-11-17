namespace Yohuke.LiveRecorder.AI.Contracts;

/// <summary>由主站固定的模型调用和上下文参数，不包含密钥或自动调度配置。</summary>
public sealed record AiExecutionSettings
{
    public string Provider { get; set; } = "OpenAI";
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string Model { get; set; } = "";
    public int TokensPerHour { get; set; } = 20000;
    public int MinimumTokens { get; set; } = 10000;
    public int MaximumTokens { get; set; } = 1000000;
    public int MaxOutputTokens { get; set; } = 2048;
    public int ContextTokenTarget { get; set; } = 32000;
    public int ContextWindowTokens { get; set; }
    public int MaxModelRequests { get; set; }
    public int MaxToolCalls { get; set; }
    public int RequestTimeoutSeconds { get; set; } = 180;
    public int MaxRequestRetries { get; set; } = 2;
    public string ThinkingMode { get; set; } = "Default";
    public string ReasoningEffort { get; set; } = "Default";
}
