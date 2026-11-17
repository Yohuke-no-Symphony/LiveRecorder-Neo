using System.Globalization;
using Yohuke.LiveRecorder.Application.Localization;

namespace Yohuke.LiveRecorder.Application.AI;

/// <summary>将 AI 失败标识转换为三语业务错误，不暴露服务端响应正文。</summary>
public static class AiErrors
{
    /// <summary>创建带稳定消息标识的 HTTP 业务错误。</summary>
    public static InfrastructureException CreateException(string code, int statusCode = 400)
    {
        return HttpStatus(code) is { } status
            ? new(MessageCode.AI_PROVIDER_REJECTED, statusCode, status)
            : new(Parse(code), statusCode);
    }

    /// <summary>按当前请求语言描述持久任务错误。</summary>
    public static string? Describe(string? code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return null;
        }
        return HttpStatus(code) is { } status ? LocalizedText.Get(MessageCode.AI_PROVIDER_REJECTED, status) : LocalizedText.Get(Parse(code));
    }

    private static MessageCode Parse(string code) => Enum.TryParse<MessageCode>(code, out var parsed) && Enum.IsDefined(parsed)
        ? parsed : MessageCode.AI_REQUEST_FAILED;

    private static int? HttpStatus(string code) => code.StartsWith("AI_HTTP_", StringComparison.Ordinal) &&
        int.TryParse(code.AsSpan(8), NumberStyles.None, CultureInfo.InvariantCulture, out var status) && status is >= 100 and <= 599 ? status : null;
}
