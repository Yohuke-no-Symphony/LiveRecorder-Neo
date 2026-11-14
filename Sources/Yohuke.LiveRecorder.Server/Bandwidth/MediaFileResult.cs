using System.Security.Claims;
using Microsoft.Net.Http.Headers;

namespace Yohuke.LiveRecorder.Server.Bandwidth;

internal sealed class MediaFileResult(string path, string contentType, MediaTrafficKind kind, string? downloadName = null) : IResult
{
    /// <inheritdoc />
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
                     ?? throw new InvalidOperationException("媒体交付需要已认证的账号。");
        var scheduler = httpContext.RequestServices.GetRequiredService<IMediaBandwidthScheduler>();
        var fileInfo = new FileInfo(path);
        if (!string.IsNullOrEmpty(fileInfo.LinkTarget))
        {
            fileInfo = fileInfo.ResolveLinkTarget(true) as FileInfo ?? fileInfo;
        }
        await using var source = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        });
        await using var stream = new BandwidthReadStream(source, scheduler, userId, kind);
        httpContext.Response.Headers.CacheControl = "private, no-store";
        httpContext.Response.Headers["X-Accel-Buffering"] = "no";
        try
        {
            await Results.File(stream, contentType, downloadName, lastModified: fileInfo.LastWriteTimeUtc, enableRangeProcessing: true)
                .ExecuteAsync(httpContext);
        }
        catch when (!httpContext.Response.HasStarted)
        {
            httpContext.Response.ContentLength = null;
            httpContext.Response.Headers.Remove(HeaderNames.ContentDisposition);
            httpContext.Response.Headers.Remove(HeaderNames.ContentRange);
            httpContext.Response.Headers.Remove(HeaderNames.AcceptRanges);
            httpContext.Response.Headers.Remove(HeaderNames.LastModified);
            throw;
        }
    }
}
