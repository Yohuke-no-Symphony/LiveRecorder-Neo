namespace Yohuke.LiveRecorder.AI.Contracts;

/// <summary>不包含模型密钥或主站文件路径的 AI 任务执行租约。</summary>
public sealed record AiAssignment
{
    public string JobId { get; set; } = "";
    public int Attempt { get; set; }
    public string LeaseToken { get; set; } = "";
    public int LeaseSeconds { get; set; } = 90;
    public string InputFingerprint { get; set; } = "";
    public string ConfigurationFingerprint { get; set; } = "";
    public bool IsConnectionTest { get; set; }
}
