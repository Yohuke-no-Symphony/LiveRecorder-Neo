using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application.Bilibili;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Persistence;

namespace Yohuke.LiveRecorder.Infrastructure.Bilibili;

/// <summary>管理员直播间搜索和账号绑定接口，不接受或返回 Cookie。</summary>
public static class BilibiliEndpointExtensions
{
    /// <summary>映射直播间搜索、账号管理及平台登录流程。</summary>
    public static IEndpointRouteBuilder MapBilibiliEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/admin/bilibili").RequireAuthorization(policy => policy.RequireRole("Admin"));
        group.MapGet("/rooms/search", async (string? keyword, int? page, string? accountId, IBilibiliService service, CancellationToken ct) => Results.Ok(await service.SearchRoomsAsync(keyword ?? "", page ?? 1, accountId, ct)));
        group.MapGet("/accounts", async (IBilibiliService service, CancellationToken ct) => Results.Ok(await service.ListAccountsAsync(ct)));
        group.MapGet("/accounts/{id}/avatar", async (string id, IMongoContext context, ArtworkCache artwork, CancellationToken ct) =>
        {
            var source = await context.Collection<BilibiliAccount>("bilibili_accounts").Find(x => x.Id == id).Project(x => x.AvatarUrl).FirstOrDefaultAsync(ct);
            var path = string.IsNullOrWhiteSpace(source) ? null : await artwork.GetAsync(source, ct);
            return path is null ? Results.NotFound() : Results.File(path, await File.ReadAllTextAsync(path + ".type", ct));
        });
        group.MapDelete("/accounts/{id}", async (string id, IBilibiliService service, CancellationToken ct) =>
        {
            await service.DeleteAccountAsync(id, ct);
            return Results.NoContent();
        });
        group.MapPost("/qr", async (IBilibiliService service, CancellationToken ct) => Results.Ok(await service.BeginQrAsync(ct)));
        group.MapGet("/qr/{sessionId}", async (string sessionId, IBilibiliService service, CancellationToken ct) => Results.Ok(await service.PollQrAsync(sessionId, ct)));
        group.MapPost("/sms", async (BilibiliSmsRequest request, IBilibiliService service, CancellationToken ct) => Results.Ok(await service.BeginSmsAsync(request, ct)));
        group.MapPost("/sms/{sessionId}/send", async (string sessionId, BilibiliChallengeResponse request, IBilibiliService service, CancellationToken ct) => Results.Ok(await service.SendSmsAsync(sessionId, request, ct)));
        group.MapPost("/sms/{sessionId}/complete", async (string sessionId, BilibiliSmsCodeRequest request, IBilibiliService service, CancellationToken ct) => Results.Ok(await service.CompleteSmsAsync(sessionId, request.Code, ct)));
        return endpoints;
    }
}
