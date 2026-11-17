using System.Text.RegularExpressions;

namespace Yohuke.LiveRecorder.Infrastructure.Analytics;

/// <summary>定义统计的播放终点、选区、同传规则及贡献观测保留方式。</summary>
public sealed record AnalyticsProjectionOptions
{
    /// <summary>已固定的播放终点，右侧事件在统计前排除。</summary>
    public double? FixedEndMs { get; init; }

    /// <summary>未固定终点时已知的逻辑场次长度，仍可由范围内事件延伸。</summary>
    public double InitialEndMs { get; init; }

    /// <summary>包含的逻辑场次选区起点。</summary>
    public double? FromMs { get; init; }

    /// <summary>排除的逻辑场次选区终点。</summary>
    public double? ToMs { get; init; }

    /// <summary>用于识别同传正文的已验证正则规则。</summary>
    public Regex? TranslationPattern { get; init; }

    /// <summary>保留贡献明细所需观测，并生成完整用户榜单。</summary>
    public bool CaptureContributions { get; init; }
}
