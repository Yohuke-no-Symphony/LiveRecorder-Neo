using System.Globalization;
using System.Resources;

namespace Yohuke.LiveRecorder.Application.Localization;

/// <summary>按请求文化解析稳定消息标识，并为旧数据保留原始诊断。</summary>
public static class LocalizedText
{
    private static readonly ResourceManager resources = new("Yohuke.LiveRecorder.Application.Localization.Messages", typeof(LocalizedText).Assembly);

    /// <summary>按当前界面文化读取参数化系统消息。</summary>
    public static string Get(MessageCode code, params object[] arguments)
    {
        return Format(code.ToString(), code.ToString(), arguments.Select(value => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "").ToArray());
    }

    /// <summary>读取参数化消息；未知标识和历史数据返回原始文本。</summary>
    public static string Format(string? code, string fallback, IReadOnlyList<string>? arguments = null, CultureInfo? culture = null)
    {
        if (string.IsNullOrEmpty(code))
        {
            return fallback;
        }
        culture ??= CultureInfo.CurrentUICulture;
        var template = resources.GetString(code, culture);
        return template is null ? fallback : string.Format(culture, template, arguments?.Cast<object>().ToArray() ?? []);
    }
}
