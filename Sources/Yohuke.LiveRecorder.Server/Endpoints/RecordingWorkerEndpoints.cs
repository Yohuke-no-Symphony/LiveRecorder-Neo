using System.Globalization;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Infrastructure.Recording;
using Yohuke.LiveRecorder.Recording.Contracts;
using Yohuke.LiveRecorder.Server.Transcoding;

namespace Yohuke.LiveRecorder.Server.Endpoints;

/// <summary>Provides recording-only transfer and durable room ownership endpoints independently of browser authentication.</summary>
public static class RecordingWorkerEndpoints
{
    /// <summary>Maps bounded append uploads, immutable blobs and coherent media and event commit acknowledgements.</summary>
    public static IEndpointRouteBuilder MapRecordingWorkerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var worker = endpoints.MapGroup("/api/worker/recording/targets/{assignmentId}").AllowAnonymous()
            .WithMetadata(new WorkerApiEndpointMetadata()).AddEndpointFilter<WorkerApiAuthenticationFilter>();
        worker.MapGet("/assignment", async (string assignmentId, HttpContext http, RecordingJobScheduler scheduler, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "private, no-store";
            return Results.Ok(await scheduler.GetAssignmentAsync(WorkerId(http), assignmentId, Generation(http), Token(http), ct,
                http.Request.Headers.Authorization.ToString()[7..]));
        });
        worker.MapPost("/sessions", async (string assignmentId, RecordingSessionDescriptor descriptor, HttpContext http,
            RecordingIngestService ingest, CancellationToken ct) =>
        {
            await ingest.RegisterSessionAsync(WorkerId(http), assignmentId, Generation(http), Token(http), descriptor, ct);
            return Results.NoContent();
        });
        MapArtifact(worker, "/sessions/{sessionId}/segments/{segmentId}/media", "media");
        MapArtifact(worker, "/sessions/{sessionId}/segments/{segmentId}/index", "index");
        MapArtifact(worker, "/sessions/{sessionId}/segments/{segmentId}/prefixes/{sha256}", "prefix");
        MapArtifact(worker, "/sessions/{sessionId}/events", "events");
        MapArtifact(worker, "/sessions/{sessionId}/event-indexes/{sha256}", "event-index");
        worker.MapPost("/sessions/{sessionId}/segments/{segmentId}/commit", async (string assignmentId, string sessionId,
            string segmentId, RecordingSegmentCommit commit, HttpContext http, RecordingIngestService ingest, CancellationToken ct) =>
        {
            if (commit.SessionId != sessionId || commit.SegmentId != segmentId)
            {
                throw new InfrastructureException("录像提交路径与身份不符。");
            }
            return Results.Ok(await ingest.CommitAsync(WorkerId(http), assignmentId, Generation(http), Token(http), commit, ct));
        });
        worker.MapPost("/sessions/{sessionId}/events/commit", async (string assignmentId, string sessionId,
            RecordingEventCommit commit, HttpContext http, RecordingIngestService ingest, CancellationToken ct) =>
            Results.Ok(await ingest.CommitEventsAsync(WorkerId(http), assignmentId, Generation(http), Token(http), sessionId, commit, ct)));
        worker.MapPost("/stopped", async (string assignmentId, RecordingStopAcknowledgement acknowledgement, HttpContext http,
            RecordingIngestService ingest, CancellationToken ct) =>
        {
            await ingest.StopAsync(WorkerId(http), assignmentId, Generation(http), Token(http), acknowledgement, ct);
            return Results.NoContent();
        });
        return endpoints;
    }

    private static void MapArtifact(RouteGroupBuilder worker, string pattern, string kind)
    {
        worker.MapMethods(pattern, [HttpMethods.Head], async (string assignmentId, string sessionId, HttpContext http,
            RecordingIngestService ingest, CancellationToken ct) =>
        {
            var offset = await ingest.GetOffsetAsync(WorkerId(http), assignmentId, Generation(http), Token(http), sessionId,
                SegmentId(http), kind, Hash(http), Number(http, "Upload-Length"), ct);
            http.Response.Headers["Upload-Offset"] = offset.ToString(CultureInfo.InvariantCulture);
            return Results.NoContent();
        });
        worker.MapPut(pattern, async (string assignmentId, string sessionId, HttpContext http, RecordingIngestService ingest, CancellationToken ct) =>
        {
            if (http.Request.ContentLength is null or <= 0 or > 8 * 1024 * 1024)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }
            var offset = await ingest.UploadAsync(WorkerId(http), assignmentId, Generation(http), Token(http), sessionId, SegmentId(http),
                kind, Hash(http), Number(http, "Upload-Offset"), Number(http, "Upload-Length"), http.Request.Body, ct);
            http.Response.Headers["Upload-Offset"] = offset.ToString(CultureInfo.InvariantCulture);
            return Results.NoContent();
        });
    }

    private static string WorkerId(HttpContext http) => http.Request.Headers["X-Worker-Id"].ToString();
    private static string SegmentId(HttpContext http) => http.Request.RouteValues["segmentId"]?.ToString() ?? "";
    private static string? Hash(HttpContext http) => http.Request.RouteValues["sha256"]?.ToString()?.ToLowerInvariant();
    private static string Token(HttpContext http)
    {
        var value = http.Request.Headers["X-Worker-Token"].ToString();
        return value.Length is > 0 and <= 256 ? value : throw new InfrastructureException("录像归属token无效。");
    }
    private static int Generation(HttpContext http) => int.TryParse(http.Request.Headers["X-Worker-Generation"], CultureInfo.InvariantCulture,
        out var value) && value > 0 ? value : throw new InfrastructureException("录像归属代次无效。");
    private static long Number(HttpContext http, string name) => long.TryParse(http.Request.Headers[name], CultureInfo.InvariantCulture,
        out var value) && value >= 0 ? value : throw new InfrastructureException("录像上传边界无效。");
}
