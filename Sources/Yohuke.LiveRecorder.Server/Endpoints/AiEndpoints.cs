using System.Security.Claims;
using Yohuke.LiveRecorder.Application.Auth;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.AI;
using Yohuke.LiveRecorder.Server.Contracts;

namespace Yohuke.LiveRecorder.Server.Endpoints;

/// <summary>分别授权共享报告查看与会产生模型用量的分析操作。</summary>
public static class AiEndpoints
{
    /// <summary>注册共享分析读取、显式创建和取消端点。</summary>
    public static IEndpointRouteBuilder MapAiEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api").RequireAuthorization(UserPermissionNames.VIEW_RECORDINGS);
        api.MapGet("/ai/jobs", async (HttpContext http, AiAnalysisService service, CancellationToken ct) => Results.Ok(await service.ListAsync(Has(http, UserPermissionNames.VIEW_ANALYTICS), ct)));
        api.MapGet("/sessions/{id}/ai-analysis", async (string id, HttpContext http, AiAnalysisService service, CancellationToken ct) =>
            Results.Ok(await service.GetAsync(id, Has(http, UserPermissionNames.GENERATE_ANALYTICS), ct))).RequireAuthorization(UserPermissionNames.VIEW_ANALYTICS);
        api.MapPost("/sessions/{id}/ai-analysis", async (string id, CreateAiAnalysisRequest request, HttpContext http, AiAnalysisService service, CancellationToken ct) =>
            Results.Ok(await service.CreateAsync(id, http.User.FindFirstValue(ClaimTypes.NameIdentifier)!, http.User.Identity?.Name ?? "", AiJobPurposes.ANALYSIS, request.Force, ct)))
            .RequireAuthorization(UserPermissionNames.VIEW_ANALYTICS, UserPermissionNames.GENERATE_ANALYTICS);
        api.MapGet("/sessions/{id}/ai-live-markers", async (string id, HttpContext http, AiAnalysisService service, CancellationToken ct) =>
            Results.Ok(await service.GetAsync(id, AiJobPurposes.LIVE_MARKERS, Has(http, UserPermissionNames.GENERATE_LIVE_MARKERS), ct)));
        api.MapPost("/sessions/{id}/ai-live-markers", async (string id, CreateAiAnalysisRequest request, HttpContext http, AiAnalysisService service, CancellationToken ct) =>
            Results.Ok(await service.CreateAsync(id, http.User.FindFirstValue(ClaimTypes.NameIdentifier)!, http.User.Identity?.Name ?? "", AiJobPurposes.LIVE_MARKERS, request.Force, ct)))
            .RequireAuthorization(UserPermissionNames.GENERATE_LIVE_MARKERS);
        api.MapPost("/ai/jobs/{id}/cancel", async (string id, HttpContext http, AiAnalysisService service, CancellationToken ct) =>
        {
            await service.CancelAsync(id, Has(http, UserPermissionNames.VIEW_ANALYTICS) && Has(http, UserPermissionNames.GENERATE_ANALYTICS), Has(http, UserPermissionNames.GENERATE_LIVE_MARKERS), ct);
            return Results.NoContent();
        });
        return endpoints;
    }

    private static bool Has(HttpContext http, string permission) => http.User.IsInRole("Admin") || http.User.HasClaim(UserPermissionNames.CLAIM_TYPE, permission);
}
