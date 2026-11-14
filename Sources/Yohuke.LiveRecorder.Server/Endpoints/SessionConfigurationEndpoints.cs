using Yohuke.LiveRecorder.Application.Auth;
using Yohuke.LiveRecorder.Application.Library;
using Yohuke.LiveRecorder.Infrastructure.Library;

namespace Yohuke.LiveRecorder.Server.Endpoints;

/// <summary>录像共享配置的独立权限边界。</summary>
public static class SessionConfigurationEndpoints
{
    /// <summary>注册录像配置读取和修改端点。</summary>
    public static IEndpointRouteBuilder MapSessionConfigurationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var sessions = endpoints.MapGroup("/api/sessions").RequireAuthorization(UserPermissionNames.VIEW_RECORDINGS);
        sessions.MapGet("/{id}/configuration", async (string id, SessionConfigurationService configuration, CancellationToken ct) =>
            Results.Ok(await configuration.GetAsync(id, ct)));
        sessions.MapPut("/{id}/configuration", async (string id, SessionConfigurationRequest request, SessionConfigurationService configuration, CancellationToken ct) =>
            Results.Ok(await configuration.UpdateAsync(id, request, ct))).RequireAuthorization(UserPermissionNames.CONFIGURE_RECORDINGS);
        return endpoints;
    }
}
