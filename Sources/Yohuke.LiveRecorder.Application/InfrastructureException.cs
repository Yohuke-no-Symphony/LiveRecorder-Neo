using System.Globalization;
using Yohuke.LiveRecorder.Application.Localization;

namespace Yohuke.LiveRecorder.Application;

/// <summary>可安全反馈到请求方的业务失败。</summary>
public sealed class InfrastructureException : Exception
{
    /// <summary>创建保留原始诊断文本的业务失败。</summary>
    public InfrastructureException(string message, int statusCode = 400) : base(message)
    {
        StatusCode = statusCode;
    }

    /// <summary>创建可根据请求语言显示的参数化业务失败。</summary>
    public InfrastructureException(MessageCode code, int statusCode = 400, params object[] arguments)
        : this(LocalizedText.Format(code.ToString(), code.ToString(), arguments.Select(value => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "").ToArray(), CultureInfo.InvariantCulture), statusCode)
    {
        Code = code.ToString();
        Arguments = arguments.Select(value => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "").ToArray();
    }

    public int StatusCode { get; }
    public string? Code { get; }
    public IReadOnlyList<string> Arguments { get; } = [];
}
