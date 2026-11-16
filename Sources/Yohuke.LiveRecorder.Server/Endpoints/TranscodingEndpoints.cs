using System.Globalization;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Infrastructure.Transcoding;
using Yohuke.LiveRecorder.Server.Contracts;
using Yohuke.LiveRecorder.Server.Transcoding;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Server.Endpoints;

/// <summary>管理员调度、Worker 文件传输与可撤销身份管理。</summary>
public static class TranscodingEndpoints
{
    /// <summary>注册与浏览器权限隔离的 Worker 端点和管理员入口。</summary>
    public static IEndpointRouteBuilder MapTranscodingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var admin = endpoints.MapGroup("/api/transcoding").RequireAuthorization(policy => policy.RequireRole("Admin"));
        admin.MapGet("/workers", async (TranscodingCoordinator coordinator, WorkerConnectionStatusTracker connections, CancellationToken ct) =>
            Results.Ok((await coordinator.ListWorkersAsync(ct)).Select(worker => WorkerView(worker, connections))));
        admin.MapGet("/workers/{id}/logs", async (string id, long? after, int? take, TranscodingCoordinator coordinator, CancellationToken ct) =>
            Results.Ok(await coordinator.ReadWorkerLogsAsync(id, after, take ?? 200, ct)));
        admin.MapPost("/workers", async (CreateTranscodeWorkerRequest request, TranscodingCoordinator coordinator, WorkerConnectionStatusTracker connections, CancellationToken ct) =>
        {
            RequireName(request.Name);
            var created = await coordinator.CreateWorkerAsync(request.Name.Trim(), request.Kind, ct);
            return Results.Ok(new { worker = WorkerView(created.Worker, connections), secret = created.Secret });
        });
        admin.MapPut("/workers/{id}", async (string id, UpdateTranscodeWorkerRequest request, TranscodingCoordinator coordinator, WorkerConnectionStatusTracker connections, CancellationToken ct) =>
        {
            RequireName(request.Name);
            try
            {
                var worker = (await coordinator.ListWorkersAsync(ct)).FirstOrDefault(value => value.Id == id)
                    ?? throw new InfrastructureException("Worker 不存在。", 404);
                WorkerConfigurationPolicy.Validate(request.Configuration, worker.Kind);
            }
            catch (ArgumentException)
            {
                throw new InfrastructureException(MessageCode.REQUEST_INVALID);
            }
            return Results.Ok(WorkerView(await coordinator.UpdateWorkerAsync(id, request.Name.Trim(), request.Enabled,
                request.Configuration, request.ExpectedRevision, ct), connections));
        });
        admin.MapPost("/workers/{id}/rotate-key", async (string id, TranscodingCoordinator coordinator, CancellationToken ct) =>
            Results.Ok(new { secret = await coordinator.RotateKeyAsync(id, ct) }));
        admin.MapDelete("/workers/{id}", async (string id, TranscodingCoordinator coordinator, CancellationToken ct) =>
        {
            await coordinator.DeleteWorkerAsync(id, ct);
            return Results.NoContent();
        });
        admin.MapGet("/jobs", async (string? sessionId, TranscodingCoordinator coordinator, CancellationToken ct) =>
            Results.Ok(await coordinator.ListJobsAsync(sessionId, ct)));
        admin.MapPost("/jobs", async (QueueTranscodeRequest request, TranscodingCoordinator coordinator, CancellationToken ct) =>
        {
            if (request.SessionIds is not { Count: > 0 and <= 200 }
                || request.SessionIds.Any(string.IsNullOrWhiteSpace) || request.QualityIds?.Count > 32)
            {
                throw new InfrastructureException(MessageCode.REQUEST_INVALID);
            }
            return Results.Ok(new { queued = await coordinator.QueueSessionsAsync(request.SessionIds, request.QualityIds, request.Force, ct) });
        });
        admin.MapPost("/jobs/{id}/cancel", async (string id, TranscodingCoordinator coordinator, CancellationToken ct) =>
        {
            await coordinator.CancelJobAsync(id, ct);
            return Results.NoContent();
        });
        admin.MapPost("/jobs/{id}/retry", async (string id, TranscodingCoordinator coordinator, CancellationToken ct) =>
        {
            await coordinator.RetryJobAsync(id, ct);
            return Results.NoContent();
        });

        var worker = endpoints.MapGroup("/api/worker/transcoding/jobs")
            .AllowAnonymous().WithMetadata(new WorkerApiEndpointMetadata()).AddEndpointFilter<WorkerApiAuthenticationFilter>();
        worker.MapMethods("/{id}/input", [HttpMethods.Get, HttpMethods.Head],
            async (string id, HttpContext http, TranscodingCoordinator coordinator, CancellationToken ct) =>
            {
                var workerId = WorkerId(http);
                var attempt = Attempt(http);
                var lease = Lease(http);
                var input = await coordinator.GetInputAsync(workerId, id, attempt, lease, ct);
                var secret = http.Request.Headers.Authorization.ToString()[7..];
                return new WorkerInputResult(input, async token =>
                {
                    if (!await coordinator.AuthenticateAsync(workerId, secret, token))
                    {
                        throw new InfrastructureException(MessageCode.REQUEST_UNAUTHORIZED, 401);
                    }
                    await coordinator.GetInputAsync(workerId, id, attempt, lease, token);
                });
            });
        worker.MapMethods("/{id}/artifacts/{fileName}", [HttpMethods.Head],
            async (string id, string fileName, HttpContext http, TranscodingCoordinator coordinator, CancellationToken ct) =>
            {
                var offset = await coordinator.GetUploadOffsetAsync(WorkerId(http), id, Attempt(http), Lease(http),
                    fileName, HeaderNumber(http, "Upload-Length"), ct);
                http.Response.Headers["Upload-Offset"] = offset.ToString(CultureInfo.InvariantCulture);
                return Results.NoContent();
            });
        worker.MapPut("/{id}/artifacts/{fileName}",
            async (string id, string fileName, HttpContext http, TranscodingCoordinator coordinator, CancellationToken ct) =>
            {
                if (http.Request.ContentLength is null or <= 0 or > 8 * 1024 * 1024)
                {
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                }
                var offset = await coordinator.UploadAsync(WorkerId(http), id, Attempt(http), Lease(http), fileName,
                    HeaderNumber(http, "Upload-Offset"), HeaderNumber(http, "Upload-Length"), http.Request.Body, ct);
                http.Response.Headers["Upload-Offset"] = offset.ToString(CultureInfo.InvariantCulture);
                return Results.NoContent();
            });
        worker.MapPost("/{id}/complete", async (string id, TranscodeOutputManifest manifest, HttpContext http,
            TranscodingCoordinator coordinator, CancellationToken ct) =>
        {
            await coordinator.CompleteAsync(WorkerId(http), id, Attempt(http), Lease(http), manifest, ct);
            return Results.NoContent();
        });
        worker.MapPost("/{id}/result", async (string id, WorkerJobResult result, HttpContext http,
            TranscodingCoordinator coordinator, CancellationToken ct) =>
        {
            if ((result.State is not ("Failed" or "Skipped")
                    && !(result.State == "Queued" && result.ErrorCode == "WorkerCapacityUnavailable"))
                || result.Error?.Length > 4096 || result.ErrorCode?.Length > 128)
            {
                throw new InfrastructureException(MessageCode.REQUEST_INVALID);
            }
            await coordinator.FinishAsync(WorkerId(http), id, Attempt(http), Lease(http), result, ct);
            return Results.NoContent();
        });
        return endpoints;
    }

    private static object WorkerView(TranscodeWorker worker, WorkerConnectionStatusTracker connections) => new
    {
        worker.Id, worker.Name, worker.Kind, worker.Enabled,
        Configuration = WorkerConfigurationProjection.Project(worker.Configuration, worker.Kind), worker.ConfigurationRevision,
        worker.AppliedConfigurationRevision, worker.Capabilities, worker.Telemetry, worker.LastSeenAtUtc, worker.ConfigurationError,
        IsOnline = connections.IsOnline(worker.Id)
    };

    private static string WorkerId(HttpContext http) => http.Request.Headers["X-Worker-Id"].ToString();

    private static string Lease(HttpContext http)
    {
        var lease = http.Request.Headers["X-Transcode-Lease"].ToString();
        return lease.Length is > 0 and <= 256 ? lease : throw new InfrastructureException(MessageCode.REQUEST_INVALID);
    }

    private static int Attempt(HttpContext http)
    {
        return int.TryParse(http.Request.Headers["X-Transcode-Attempt"], CultureInfo.InvariantCulture, out var value) && value > 0
            ? value : throw new InfrastructureException(MessageCode.REQUEST_INVALID);
    }

    private static long HeaderNumber(HttpContext http, string name)
    {
        return long.TryParse(http.Request.Headers[name], CultureInfo.InvariantCulture, out var value) && value >= 0
            ? value : throw new InfrastructureException(MessageCode.REQUEST_INVALID);
    }

    private static void RequireName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100)
        {
            throw new InfrastructureException(MessageCode.REQUEST_INVALID);
        }
    }
}
