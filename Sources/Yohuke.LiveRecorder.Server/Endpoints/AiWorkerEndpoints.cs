using System.Globalization;
using Yohuke.LiveRecorder.AI.Contracts;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Infrastructure.AI;
using Yohuke.LiveRecorder.Server.Transcoding;

namespace Yohuke.LiveRecorder.Server.Endpoints;

/// <summary>向独立 AI 节点交付固定输入并接收受租约约束的检查点及结果。</summary>
public static class AiWorkerEndpoints
{
    /// <summary>注册 AI 专属执行接口，使用与现有节点相同的可撤销身份认证。</summary>
    public static IEndpointRouteBuilder MapAiWorkerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var worker = endpoints.MapGroup("/api/worker/ai/jobs/{jobId}").AllowAnonymous()
            .WithMetadata(new WorkerApiEndpointMetadata()).AddEndpointFilter<WorkerApiAuthenticationFilter>();
        worker.MapGet("/execution", async (string jobId, HttpContext http, AiJobExecutionService service, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "private, no-store";
            return Results.Ok(await service.GetExecutionAsync(WorkerId(http), jobId, Attempt(http), Token(http), ct));
        });
        worker.MapGet("/sources/{sourceId}", async (string jobId, string sourceId, HttpContext http, AiJobExecutionService service, CancellationToken ct) =>
        {
            var asset = await service.GetSourceAsync(WorkerId(http), jobId, Attempt(http), Token(http), sourceId, ct);
            http.Response.Headers.CacheControl = "private, no-store";
            http.Response.ContentLength = asset.Length;
            return Results.Stream(async output =>
            {
                await using var input = new FileStream(asset.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, true);
                var remaining = asset.Length;
                var buffer = new byte[65536];
                while (remaining > 0)
                {
                    var count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(remaining, buffer.Length)), ct);
                    if (count == 0)
                    {
                        throw new EndOfStreamException("The fixed AI archive source was truncated during transfer.");
                    }
                    await output.WriteAsync(buffer.AsMemory(0, count), ct);
                    remaining -= count;
                }
            }, "application/x-ndjson");
        });
        worker.MapPost("/checkpoint", async (string jobId, AiExecutionCheckpoint checkpoint, HttpContext http,
            AiJobExecutionService service, CancellationToken ct) =>
            Results.Ok(await service.CheckpointAsync(WorkerId(http), jobId, Attempt(http), Token(http), checkpoint, ct)));
        worker.MapPost("/complete", async (string jobId, AiExecutionResult result, HttpContext http,
            AiJobExecutionService service, CancellationToken ct) =>
            Results.Ok(await service.CompleteAsync(WorkerId(http), jobId, Attempt(http), Token(http), result, ct)));
        return endpoints;
    }

    private static string WorkerId(HttpContext http) => http.Request.Headers["X-Worker-Id"].ToString();

    private static int Attempt(HttpContext http) => int.TryParse(http.Request.Headers["X-Ai-Attempt"], NumberStyles.None,
        CultureInfo.InvariantCulture, out var attempt) && attempt > 0 ? attempt : throw new InfrastructureException("Invalid AI execution attempt.", 400);

    private static string Token(HttpContext http)
    {
        var token = http.Request.Headers["X-Ai-Lease-Token"].ToString();
        return token.Length is > 0 and <= 256 ? token : throw new InfrastructureException("Invalid AI execution lease.", 400);
    }
}
