using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Infrastructure.Transcoding;
using Yohuke.LiveRecorder.Server.Localization;

namespace Yohuke.LiveRecorder.Server.Transcoding;

/// <summary>Worker 传输接口只接受 TLS 上独立的可撤销凭据。</summary>
public sealed class WorkerApiAuthenticationFilter(TranscodingCoordinator coordinator) : IEndpointFilter
{
    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var workerId = http.Request.Headers["X-Worker-Id"].ToString();
        var authorization = http.Request.Headers.Authorization.ToString();
        if (!http.Request.IsHttps || workerId.Length is < 1 or > 128 || !authorization.StartsWith("Bearer ", StringComparison.Ordinal)
            || authorization.Length > 1024
            || !await coordinator.AuthenticateAsync(workerId, authorization[7..], http.RequestAborted))
        {
            return LocalizedProblem.Create(MessageCode.REQUEST_UNAUTHORIZED, 401);
        }
        return await next(context);
    }
}
