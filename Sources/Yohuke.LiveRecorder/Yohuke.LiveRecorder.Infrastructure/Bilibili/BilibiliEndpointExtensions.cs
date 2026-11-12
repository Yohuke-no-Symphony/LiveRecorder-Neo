using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Yohuke.LiveRecorder.Application.Bilibili;

namespace Yohuke.LiveRecorder.Infrastructure.Bilibili;

/// <summary>管理员扫码和短信绑定接口，不接受或返回 Cookie。</summary>
public static class BilibiliEndpointExtensions
{
    /// <summary>映射账号列表、删除及平台登录流程。</summary>
    public static IEndpointRouteBuilder MapBilibiliEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/admin/bilibili").RequireAuthorization(policy => policy.RequireRole("Admin"));
        group.MapGet("/accounts", async (IBilibiliService service, CancellationToken ct) => Results.Ok(await service.ListAccountsAsync(ct)));
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
