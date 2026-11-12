namespace Yohuke.LiveRecorder.Application;

/// <summary>可安全反馈到请求方的业务失败。</summary>
public sealed class InfrastructureException(string message, int statusCode = 400) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
