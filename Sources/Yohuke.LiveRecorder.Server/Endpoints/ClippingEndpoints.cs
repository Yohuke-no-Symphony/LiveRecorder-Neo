using System.Globalization;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Clipping.Contracts;
using Yohuke.LiveRecorder.Infrastructure.Clipping;
using Yohuke.LiveRecorder.Infrastructure.Transcoding;
using Yohuke.LiveRecorder.Server.Clipping;
using Yohuke.LiveRecorder.Server.Transcoding;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Server.Endpoints;

/// <summary>Provides Worker-authenticated bounded clipping downloads and resumable output uploads.</summary>
public static class ClippingEndpoints
{
    /// <summary>Maps clipping transport endpoints using the shared revocable Worker identity.</summary>
    public static IEndpointRouteBuilder MapClippingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var worker = endpoints.MapGroup("/api/worker/clipping/jobs").AllowAnonymous()
            .WithMetadata(new WorkerApiEndpointMetadata()).AddEndpointFilter<WorkerApiAuthenticationFilter>();
        worker.MapMethods("/{id}/inputs/{inputId}", [HttpMethods.Get, HttpMethods.Head],
            async (string id, string inputId, HttpContext http, ClipTransferService transfers, TranscodingCoordinator coordinator,
                CancellationToken ct) =>
            {
                var workerId = WorkerId(http);
                var attempt = Attempt(http);
                var lease = Lease(http);
                var input = await transfers.GetInputAsync(workerId, id, attempt, lease, inputId, ct);
                var secret = http.Request.Headers.Authorization.ToString()[7..];
                return new ClipInputResult(input, async token =>
                {
                    if (!await coordinator.AuthenticateAsync(workerId, secret, token))
                    {
                        throw new InfrastructureException("Worker 凭据已失效。", 401);
                    }
                    await transfers.GetInputAsync(workerId, id, attempt, lease, inputId, token);
                });
            });
        worker.MapPost("/{id}/upload-session", async (string id, WorkerUploadSession session, HttpContext http,
            ClipTransferService transfers, CancellationToken ct) =>
        {
            await transfers.BeginUploadAsync(WorkerId(http), id, Attempt(http), Lease(http), session, ct);
            return Results.NoContent();
        });
        worker.MapMethods("/{id}/output", [HttpMethods.Head],
            async (string id, HttpContext http, ClipTransferService transfers, CancellationToken ct) =>
            {
                var offset = await transfers.GetUploadOffsetAsync(WorkerId(http), id, Attempt(http), Lease(http), Number(http, "Upload-Length"), ct);
                http.Response.Headers["Upload-Offset"] = offset.ToString(CultureInfo.InvariantCulture);
                return Results.NoContent();
            });
        worker.MapPut("/{id}/output", async (string id, HttpContext http, ClipTransferService transfers, CancellationToken ct) =>
        {
            if (http.Request.ContentLength is null or <= 0 or > 8 * 1024 * 1024)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }
            var offset = await transfers.UploadAsync(WorkerId(http), id, Attempt(http), Lease(http), Number(http, "Upload-Offset"),
                Number(http, "Upload-Length"), http.Request.Body, ct);
            http.Response.Headers["Upload-Offset"] = offset.ToString(CultureInfo.InvariantCulture);
            return Results.NoContent();
        });
        worker.MapPost("/{id}/complete", async (string id, ClipOutputManifest manifest, HttpContext http, ClipTransferService transfers,
            CancellationToken ct) =>
        {
            await transfers.CompleteAsync(WorkerId(http), id, Attempt(http), Lease(http), manifest, ct);
            return Results.NoContent();
        });
        worker.MapPost("/{id}/result", async (string id, WorkerJobResult result, HttpContext http, ClipTransferService transfers,
            CancellationToken ct) =>
        {
            await transfers.FinishAsync(WorkerId(http), id, Attempt(http), Lease(http), result, ct);
            return Results.NoContent();
        });
        return endpoints;
    }

    private static string WorkerId(HttpContext http) => http.Request.Headers["X-Worker-Id"].ToString();

    private static string Lease(HttpContext http)
    {
        var value = http.Request.Headers["X-Worker-Lease"].ToString();
        return value.Length is > 0 and <= 256 ? value : throw new InfrastructureException("切片租约无效。");
    }

    private static int Attempt(HttpContext http) => int.TryParse(http.Request.Headers["X-Worker-Attempt"], CultureInfo.InvariantCulture,
        out var value) && value > 0 ? value : throw new InfrastructureException("切片执行代次无效。");

    private static long Number(HttpContext http, string name) => long.TryParse(http.Request.Headers[name], CultureInfo.InvariantCulture,
        out var value) && value >= 0 ? value : throw new InfrastructureException("切片上传偏移或长度无效。");
}
