namespace Yohuke.LiveRecorder.Domain.Localization;

/// <summary>界面与请求可使用的规范语言标识。</summary>
public static class SupportedLanguages
{
    public const string CHINESE = "zh-CN";
    public const string ENGLISH = "en-US";
    public const string JAPANESE = "ja-JP";
    public const string DEFAULT = CHINESE;

    /// <summary>判断语言是否属于网站明确支持的语言。</summary>
    public static bool IsSupported(string? language) => language is CHINESE or ENGLISH or JAPANESE;
}
