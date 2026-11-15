using System.Security.Claims;
using Yohuke.LiveRecorder.Application.Auth;
using Yohuke.LiveRecorder.Infrastructure.Storage;

namespace Yohuke.LiveRecorder.Server.Endpoints;

/// <summary>录像分段回收的管理权限边界。</summary>
public static class SessionSegmentEndpoints
{
    /// <summary>注册单个分段移入回收站的操作。</summary>
    public static IEndpointRouteBuilder MapSessionSegmentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapDelete("/api/sessions/{id}/segments/{segmentId}",
            async (string id, string segmentId, HttpContext http, SessionSegmentTrashService trash, CancellationToken ct) =>
            {
                await trash.TrashAsync(id, segmentId, http.User.FindFirstValue(ClaimTypes.NameIdentifier)!, ct);
                return Results.NoContent();
            }).RequireAuthorization(UserPermissionNames.MANAGE_RECORDINGS);
        return endpoints;
    }
}
