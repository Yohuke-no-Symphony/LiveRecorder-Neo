using System.Security.Claims;
using Yohuke.LiveRecorder.Infrastructure.Library;
using Yohuke.LiveRecorder.Server.Contracts;

namespace Yohuke.LiveRecorder.Server.Endpoints;

/// <summary>为每位成员单独交付和管理场次个人标记。</summary>
public static class SessionBookmarkEndpoints
{
    /// <summary>注册仅按当前登录身份访问的个人标记接口。</summary>
    public static IEndpointRouteBuilder MapSessionBookmarkEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var bookmarks = endpoints.MapGroup("/api/sessions/{id}/bookmarks").RequireAuthorization();
        bookmarks.MapGet("", async (string id, HttpContext http, SessionBookmarkService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListAsync(id, Owner(http), cancellationToken)));
        bookmarks.MapPost("", async (string id, SessionBookmarkRequest request, HttpContext http, SessionBookmarkService service, CancellationToken cancellationToken) =>
        {
            var bookmark = await service.CreateAsync(id, Owner(http), request.Title, request.OffsetMs, cancellationToken);
            return Results.Created($"/api/sessions/{id}/bookmarks/{bookmark.Id}", bookmark);
        });
        bookmarks.MapPut("/{bookmarkId}", async (string id, string bookmarkId, SessionBookmarkRequest request, HttpContext http, SessionBookmarkService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.UpdateAsync(id, bookmarkId, Owner(http), request.Title, request.OffsetMs, cancellationToken)));
        bookmarks.MapDelete("/{bookmarkId}", async (string id, string bookmarkId, HttpContext http, SessionBookmarkService service, CancellationToken cancellationToken) =>
        {
            await service.DeleteAsync(id, bookmarkId, Owner(http), cancellationToken);
            return Results.NoContent();
        });
        return endpoints;
    }

    private static string Owner(HttpContext http) => http.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
}
