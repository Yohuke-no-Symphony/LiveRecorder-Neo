using System.Text;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Application.AI;

/// <summary>统一配置验证及分析用量参考。</summary>
public static class AiAnalysisPolicy
{
    public const int MIN_CONTEXT_TOKEN_TARGET = 8192;
    public const string ANALYSIS_VERSION = "segmented-topics-v2";

    /// <summary>按录像时长计算本次任务的 token 参考量，不作为执行停止条件。</summary>
    public static int Budget(AiSettings settings, double durationMs) =>
        (int)Math.Clamp(Math.Ceiling(durationMs / 3600000 * settings.TokensPerHour), settings.MinimumTokens, settings.MaximumTokens);

    /// <summary>验证兼容协议地址、正整数用量参考及可不限的调用次数，不解密或记录 key。</summary>
    public static void Validate(AiSettings settings)
    {
        if (settings.MaxConcurrentJobs is < 1 or > 64 || settings.Provider is not ("OpenAI" or "DeepSeek" or "Custom") ||
            settings.TokensPerHour <= 0 || settings.MinimumTokens <= 0 || settings.MaximumTokens < settings.MinimumTokens ||
            settings.MaxOutputTokens <= 0 || settings.MaxModelRequests < 0 || settings.MaxToolCalls < 0 ||
            settings.RequestTimeoutSeconds < 0 || settings.MaxRequestRetries < 0 ||
            settings.ContextWindowTokens < 0 || settings.ContextWindowTokens > 0 &&
            (settings.ContextWindowTokens < MIN_CONTEXT_TOKEN_TARGET || settings.MaxOutputTokens >= settings.ContextWindowTokens) ||
            settings.ThinkingMode is not ("Default" or "Enabled" or "Disabled") ||
            settings.ReasoningEffort is not ("Default" or "Low" or "High" or "Max") ||
            settings.Model is null || settings.Enabled && string.IsNullOrWhiteSpace(settings.Model) || settings.Model.Length > 200)
        {
            throw AiErrors.CreateException("AI_SETTINGS_INVALID");
        }
        Endpoint(settings.BaseUrl);
    }

    /// <summary>接受基础地址或完整 Chat Completions 地址。</summary>
    public static Uri Endpoint(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw AiErrors.CreateException("AI_ENDPOINT_INVALID");
        }
        value = value.Trim().TrimEnd('/');
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw AiErrors.CreateException("AI_ENDPOINT_INVALID");
        }
        return value.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) ? uri : new(value + "/chat/completions");
    }

    /// <summary>无厂商 tokenizer 时按 Unicode 文本保守预估，并加入协议余量。</summary>
    public static int EstimateTokens(string value)
    {
        var ascii = 0;
        var nonAscii = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (rune.IsAscii)
            {
                ascii++;
            }
            else
            {
                nonAscii += rune.Utf8SequenceLength;
            }
        }
        return checked((int)Math.Ceiling(ascii / 3d) + nonAscii + 128);
    }
}
