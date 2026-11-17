namespace Yohuke.LiveRecorder.AI.Contracts;

/// <summary>固定时间范围、源身份和准备方式的 AI 输入清单。</summary>
public sealed record AiInputManifest
{
    public string Fingerprint { get; set; } = "";
    public double DurationMs { get; set; }
    public double RecordedDurationMs { get; set; }
    public string TranslationPattern { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTime StartedAtUtc { get; set; }
    public List<AiInputSource> Sources { get; set; } = [];
    public bool PreparedSnapshot { get; set; }
}
