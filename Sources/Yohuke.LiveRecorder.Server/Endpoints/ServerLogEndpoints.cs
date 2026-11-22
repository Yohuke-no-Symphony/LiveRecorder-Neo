using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Server.Localization;
using Yohuke.LiveRecorder.Server.Logging;

namespace Yohuke.LiveRecorder.Server.Endpoints;

internal static class ServerLogEndpoints
{
    internal static void MapServerLogEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/admin/server-logs", (HttpContext http, ServerLogBuffer logs) =>
        {
            http.Response.Headers.CacheControl = "private, no-store";
            var query = http.Request.Query;
            if (query.TryGetValue("cursor", out var cursors) && cursors.Count != 1)
            {
                return LocalizedProblem.Create(MessageCode.REQUEST_INVALID, StatusCodes.Status400BadRequest);
            }

            var page = logs.Read(cursors.Count == 0 ? null : cursors[0]);
            return page is null
                ? LocalizedProblem.Create(MessageCode.REQUEST_INVALID, StatusCodes.Status400BadRequest)
                : Results.Ok(page);
        }).RequireAuthorization(policy => policy.RequireRole("Admin"));
    }
}
