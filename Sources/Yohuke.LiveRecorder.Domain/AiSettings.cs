namespace Yohuke.LiveRecorder.Domain;

/// <summary>管理员维护的单一 AI 服务、用量参考和调用配置。</summary>
public sealed class AiSettings
{
    public bool Enabled { get; set; }
    public string Provider { get; set; } = "OpenAI";
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string Model { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public bool HasApiKey { get; set; }
    public bool ClearApiKey { get; set; }
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
