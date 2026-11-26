using System.Security.Claims;
using Yohuke.LiveRecorder.Application.Auth;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Library;
using Yohuke.LiveRecorder.Server.Bandwidth;
using Yohuke.LiveRecorder.Server.Contracts;

namespace Yohuke.LiveRecorder.Server.Endpoints;

/// <summary>切片分享的所有者管理和接收者访问接口。</summary>
public static class ClipShareEndpoints
{
    internal const string UNLOCK_RATE_LIMIT_POLICY = "clip-share-unlock";

    /// <summary>注册管理分享链接以及凭令牌查看和下载切片的接口。</summary>
    public static IEndpointRouteBuilder MapClipShareEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var sharing = endpoints.MapGroup("/api/clips/{id}/share").RequireAuthorization();
        sharing.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "private, no-store";
            return await next(context);
        });
        sharing.MapGet("", async (string id, HttpContext http, ClipShareService service, CancellationToken ct) =>
            LinkResponse(await service.GetSettingsAsync(id, UserId(http), CanManageAll(http), ct)));
        sharing.MapPut("", async (string id, HttpContext http, ClipShareService service, CancellationToken ct) =>
            LinkResponse(await service.CreateAsync(id, UserId(http), CanManageAll(http), ct)))
            .RequireAuthorization(UserPermissionNames.VIEW_RECORDINGS);
        sharing.MapPut("/settings", async (string id, ClipShareSettingsRequest request, HttpContext http, ClipShareService service, CancellationToken ct) =>
            LinkResponse(await service.SetSettingsAsync(id, UserId(http), CanManageAll(http), request.Token, request.Visibility, request.Password, ct)))
            .RequireAuthorization(UserPermissionNames.VIEW_RECORDINGS);
        sharing.MapDelete("/{token}", async (string id, string token, HttpContext http, ClipShareService service, CancellationToken ct) =>
        {
            await service.RevokeAsync(id, token, UserId(http), CanManageAll(http), ct);
            return Results.NoContent();
        });

        var viewing = sharing.MapGroup("/{token}").RequireAuthorization(UserPermissionNames.VIEW_RECORDINGS);
        viewing.MapPost("/unlock", async (string id, string token, ClipShareUnlockRequest request, HttpContext http,
            ClipShareService service, ClipShareAccessCookie access, CancellationToken ct) =>
        {
            if (await service.UnlockAsync(id, token, request.Password, ct))
            {
                access.Grant(http, id, token);
            }
            return Results.NoContent();
        }).RequireRateLimiting(UNLOCK_RATE_LIMIT_POLICY);
        viewing.MapGet("", async (string id, string token, HttpContext http, ClipShareService service, ClipShareAccessCookie access,
            IIdentityService identity, CancellationToken ct) =>
        {
            var (clip, _) = await service.GetSharedAsync(id, token, access.IsGranted(http, id, token), ct);
            var owner = PublicUserProfileResponse.From(await identity.GetUserAsync(clip.OwnerId, ct));
            return Results.Ok(new SharedClipResponse(clip.Title, clip.EndMs - clip.StartMs, clip.Bytes, clip.ExpiresAtUtc, owner));
        });
        viewing.MapGet("/download", async (string id, string token, HttpContext http, ClipShareService service, ClipShareAccessCookie access, CancellationToken ct) =>
        {
            var (clip, path) = await service.GetSharedAsync(id, token, access.IsGranted(http, id, token), ct);
            return new MediaFileResult(path, "video/mp4", MediaTrafficKind.Download, MediaPaths.SafeName(clip.Title) + ".mp4");
        }).RequireAuthorization(UserPermissionNames.DOWNLOAD_RECORDINGS);

        return endpoints;
    }

    private static IResult LinkResponse((string? Token, string? Visibility) state) =>
        Results.Ok(new ClipShareLinkResponse(state.Token, state.Visibility));

    private static string UserId(HttpContext http) => http.User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    private static bool CanManageAll(HttpContext http) => UserPermissionNames.IsGranted(http.User, UserPermissionNames.MANAGE_CLIPS);
}
