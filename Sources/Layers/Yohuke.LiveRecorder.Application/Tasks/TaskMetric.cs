namespace Yohuke.LiveRecorder.Application.Tasks;

/// <summary>可本地化名称及单位的公开阶段指标。</summary>
public sealed record TaskMetric(string Key, double? Value = null, string Unit = "number", string? Text = null);
