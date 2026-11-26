using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Auth;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Infrastructure.Library;
using Yohuke.LiveRecorder.Server.Contracts;

namespace Yohuke.LiveRecorder.Server.Endpoints;

internal static class UserProfileEndpoints
{
    private const int DEFAULT_LIMIT = 25;
    private const int MAX_LIMIT = 100;

    internal static IEndpointRouteBuilder MapUserProfileEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var users = endpoints.MapGroup("/api/users/{userId}").RequireAuthorization();
        users.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "private, no-store";
            return await next(context);
        });
        users.MapGet("", async (string userId, IIdentityService identity, CancellationToken ct) =>
            Results.Ok(await RequireProfileAsync(userId, identity, ct)));
        users.MapGet("/shares", ListSharesAsync).RequireAuthorization(UserPermissionNames.VIEW_RECORDINGS);
        return endpoints;
    }

    private static async Task<IResult> ListSharesAsync(string userId, DateTimeOffset? beforeUtc, string? beforeId, int? limit,
        IIdentityService identity, ClipShareService sharing, CancellationToken ct)
    {
        var size = limit ?? DEFAULT_LIMIT;
        if (size is < 1 or > MAX_LIMIT || beforeUtc.HasValue != (beforeId is not null)
            || beforeId is not null && (string.IsNullOrWhiteSpace(beforeId) || beforeId.Length > 128))
        {
            throw new InfrastructureException(MessageCode.REQUEST_INVALID);
        }
        await RequireProfileAsync(userId, identity, ct);
        var clips = await sharing.ListPublicAsync(userId, beforeUtc?.UtcDateTime, beforeId, size + 1, ct);
        var next = clips.Count > size ? new PublicClipShareCursor(clips[size - 1].CreatedAtUtc, clips[size - 1].Id) : null;
        var items = clips.Take(size).Select(clip => new PublicClipShareEntry(clip.Id, clip.ShareToken!, clip.Title,
            clip.EndMs - clip.StartMs, clip.Bytes, clip.CreatedAtUtc, clip.ExpiresAtUtc)).ToArray();
        return Results.Ok(new PublicClipSharePage(items, next));
    }

    private static async Task<PublicUserProfileResponse> RequireProfileAsync(string userId, IIdentityService identity, CancellationToken ct) =>
        PublicUserProfileResponse.From(await identity.GetUserAsync(userId, ct))
        ?? throw new InfrastructureException(MessageCode.AUTH_USER_NOT_FOUND, 404);
}
