using Microsoft.Net.Http.Headers;
using Yohuke.LiveRecorder.Infrastructure.Transcoding;
using Yohuke.LiveRecorder.Server.Bandwidth;

namespace Yohuke.LiveRecorder.Server.Transcoding;

internal sealed class WorkerInputResult(TranscodeInput input, string workerId, Func<CancellationToken, Task> validate) : IResult
{
    /// <inheritdoc />
    public async Task ExecuteAsync(HttpContext context)
    {
        var scheduler = context.RequestServices.GetRequiredService<IMediaBandwidthScheduler>();
        await using var file = new FileStream(input.Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var guarded = new LeaseValidatedReadStream(file, input.Length, validate);
        await using var stream = new BandwidthReadStream(guarded, scheduler, "worker:" + workerId, MediaTrafficKind.Download);
        context.Response.Headers.CacheControl = "private, no-store";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        var tag = input.ETag.StartsWith('"') ? input.ETag : "\"" + input.ETag + "\"";
        await Results.File(stream, "application/octet-stream", entityTag: new EntityTagHeaderValue(tag),
            enableRangeProcessing: true).ExecuteAsync(context);
    }
}
