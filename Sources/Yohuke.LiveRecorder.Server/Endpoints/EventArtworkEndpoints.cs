using Yohuke.LiveRecorder.Application.Auth;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Library;

namespace Yohuke.LiveRecorder.Server.Endpoints;

/// <summary>向已登录成员交付归档消息头像与表情图片。</summary>
public static class EventArtworkEndpoints
{
    /// <summary>注册稳定 UID 头像地址与按场次事件定位的图片地址。</summary>
    public static IEndpointRouteBuilder MapEventArtworkEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api").RequireAuthorization();
        api.MapGet("/audience/{uid:long}/avatar", async (long uid, HttpContext http, EventArtworkService artwork, CancellationToken ct) =>
        {
            return Image(await artwork.GetAudienceAvatarAsync(uid, ct), http);
        });
        var viewing = api.MapGroup("").RequireAuthorization(UserPermissionNames.VIEW_RECORDINGS);
        viewing.MapGet("/sessions/{id}/events/{sequence:long}/avatar", async (string id, long sequence, HttpContext http, LibraryService library, EventArtworkService artwork, CancellationToken ct) =>
        {
            var detail = await library.GetAsync(id, ct);
            return Image(await artwork.GetAvatarAsync(detail.Session, sequence, ct), http);
        });
        viewing.MapGet("/sessions/{id}/events/{sequence:long}/emoticons/{index:int}", async (string id, long sequence, int index, HttpContext http, LibraryService library, EventArtworkService artwork, CancellationToken ct) =>
        {
            var detail = await library.GetAsync(id, ct);
            return Image(await artwork.GetEmoticonAsync(detail.Session, sequence, index, ct), http);
        });
        return endpoints;
    }

    private static IResult Image(ArtworkCacheLease? image, HttpContext http)
    {
        if (image is null)
        {
            http.Response.Headers.CacheControl = "no-store";
            return Results.NotFound();
        }
        http.Response.Headers.CacheControl = "private, max-age=300";
        http.Response.Headers.XContentTypeOptions = "nosniff";
        return new ArtworkFileResult(image, lastModified: true);
    }
}
