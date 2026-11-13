using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Library;
using Yohuke.LiveRecorder.Infrastructure.Playback;

namespace Yohuke.LiveRecorder.Server.Endpoints;

/// <summary>为具有录播播放权限的成员交付时间轴摘要。</summary>
public static class SessionTimelineEndpoints
{
    /// <summary>注册独立于管理员财务分析的热度与互动记录接口。</summary>
    public static IEndpointRouteBuilder MapSessionTimelineEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/sessions/{id}/timeline", async (string id, int? bucketCount, LibraryService library, IAppPaths paths, SessionTimelineService timeline, CancellationToken cancellationToken) =>
        {
            var detail = await library.GetAsync(id, cancellationToken);
            return Results.Ok(await timeline.GetAsync(detail.Session, paths.RecordsDirectory, detail.DurationMs, bucketCount ?? 300, cancellationToken));
        }).RequireAuthorization();
        return endpoints;
    }
}
