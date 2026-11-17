using System.Security.Claims;
using System.Globalization;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Application.Auth;
using Yohuke.LiveRecorder.Application.Tasks;
using Yohuke.LiveRecorder.Infrastructure.Tasks;

namespace Yohuke.LiveRecorder.Server.Endpoints;

/// <summary>隔离个人任务与管理员全量任务，并提供只清理历史展示的接口。</summary>
public static class TaskHistoryEndpoints
{
    /// <summary>注册个人及管理范围的统一查询、详情与二次确认清理端点。</summary>
    public static IEndpointRouteBuilder MapTaskHistoryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        Map(endpoints.MapGroup("/api/tasks").RequireAuthorization(), false);
        Map(endpoints.MapGroup("/api/admin/worker-tasks").RequireAuthorization(policy => policy.RequireRole("Admin")), true);
        endpoints.MapGet("/api/admin/worker-tasks/filters", async (HttpContext http, TaskHistoryService tasks, CancellationToken ct) =>
        {
            NoStore(http);
            return Results.Ok(await tasks.FiltersAsync(Viewer(http), ct));
        }).RequireAuthorization(policy => policy.RequireRole("Admin"));
        endpoints.MapPost("/api/admin/worker-tasks/active", async (TaskHistoryActiveRequest request, HttpContext http, TaskHistoryService tasks, CancellationToken ct) =>
        {
            NoStore(http);
            return Results.Ok(await tasks.ActiveAsync(request, Viewer(http), ct));
        }).RequireAuthorization(policy => policy.RequireRole("Admin"));
        return endpoints;
    }

    private static void Map(RouteGroupBuilder api, bool administration)
    {
        api.MapGet("", async (HttpContext http, TaskHistoryService tasks, CancellationToken ct) =>
        {
            NoStore(http);
            return Results.Ok(await tasks.ListAsync(Query(http.Request), Viewer(http), administration, ct));
        });
        api.MapGet("/clear-completed/preview", async (HttpContext http, TaskHistoryService tasks, CancellationToken ct) =>
        {
            NoStore(http);
            return Results.Ok(await tasks.PreviewClearAsync(Query(http.Request), Viewer(http), administration, ct));
        });
        api.MapPost("/clear-completed", async (TaskHistoryClearRequest request, HttpContext http, TaskHistoryService tasks, CancellationToken ct) =>
            Results.Ok(new { removed = await tasks.ClearCompletedAsync(request, Viewer(http), administration, ct) }));
        api.MapGet("/{kind}/{id}", async (string kind, string id, HttpContext http, TaskHistoryService tasks, CancellationToken ct) =>
        {
            NoStore(http);
            return Results.Ok(await tasks.GetAsync(kind, id, Viewer(http), administration, ct));
        });
        api.MapDelete("/{kind}/{id}", async (string kind, string id, [Microsoft.AspNetCore.Mvc.FromBody] TaskHistoryDeleteRequest request,
            HttpContext http, TaskHistoryService tasks, CancellationToken ct) =>
        {
            await tasks.DeleteAsync(kind, id, request, Viewer(http), administration, ct);
            return Results.NoContent();
        });
        api.MapGet("/{kind}/{id}/logs", async (string kind, string id, string? stage, long? after, int? take, int? attempt,
            HttpContext http, TaskLogService logs, CancellationToken ct) =>
        {
            NoStore(http);
            return Results.Ok(await logs.ReadAsync(kind, id, stage, after, take ?? 200, Viewer(http), administration, ct, attempt));
        });
    }

    private static TaskHistoryViewer Viewer(HttpContext http) => new(
        http.User.FindFirstValue(ClaimTypes.NameIdentifier)!, http.User.IsInRole("Admin"),
        http.User.FindAll(UserPermissionNames.CLAIM_TYPE).Select(claim => claim.Value).ToHashSet(StringComparer.Ordinal));

    private static void NoStore(HttpContext http) => http.Response.Headers.CacheControl = "private, no-store";

    private static TaskHistoryQuery Query(HttpRequest request)
    {
        var query = request.Query;
        var limit = 50;
        if (query.TryGetValue("limit", out var raw) && !int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out limit))
        {
            throw new InfrastructureException(MessageCode.REQUEST_INVALID);
        }
        return new()
        {
            State = query["state"], Kind = query["kind"], Type = query["type"], Origin = query["origin"], WorkerId = query["workerId"],
            InitiatorUserId = query["initiatorUserId"], Search = query["search"], Cursor = query["cursor"], Limit = limit
        };
    }
}
