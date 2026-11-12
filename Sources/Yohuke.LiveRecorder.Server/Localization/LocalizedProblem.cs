using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Localization;

namespace Yohuke.LiveRecorder.Server.Localization;

/// <summary>输出带稳定错误标识和请求语言文本的 Problem Details。</summary>
public static class LocalizedProblem
{
    /// <summary>将业务失败转换为当前请求语言的错误响应。</summary>
    public static IResult Create(InfrastructureException error)
    {
        return Results.Problem(statusCode: error.StatusCode,
            detail: LocalizedText.Format(error.Code, error.Message, error.Arguments),
            extensions: new Dictionary<string, object?> { ["code"] = error.Code, ["arguments"] = error.Arguments });
    }

    /// <summary>为协议和基础设施失败生成参数化错误响应。</summary>
    public static IResult Create(MessageCode code, int statusCode, params object[] arguments)
    {
        return Results.Problem(statusCode: statusCode, detail: LocalizedText.Get(code, arguments),
            extensions: new Dictionary<string, object?> { ["code"] = code.ToString(), ["arguments"] = arguments });
    }
}
