using System.Text.RegularExpressions;
using Yohuke.LiveRecorder.Application.Localization;

namespace Yohuke.LiveRecorder.Application.Library;

/// <summary>统一校验同传筛选规则，并为匹配设置有限执行时间。</summary>
public static class TranslationDanmuPattern
{
    private const int MAX_PATTERN_LENGTH = 2048;
    private const int MATCH_TIMEOUT_MS = 100;

    /// <summary>创建区分大小写的正文正则；非法或空白规则返回可本地化的配置错误。</summary>
    public static Regex Create(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern.Length > MAX_PATTERN_LENGTH)
        {
            throw new InfrastructureException(MessageCode.TRANSLATION_DANMU_PATTERN_INVALID);
        }
        try
        {
            return new(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(MATCH_TIMEOUT_MS));
        }
        catch (ArgumentException)
        {
            throw new InfrastructureException(MessageCode.TRANSLATION_DANMU_PATTERN_INVALID);
        }
    }
}
