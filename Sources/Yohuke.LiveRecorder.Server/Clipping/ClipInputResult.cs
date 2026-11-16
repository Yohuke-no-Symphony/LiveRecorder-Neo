using System.Globalization;
using System.Net.Http.Headers;
using Yohuke.LiveRecorder.Infrastructure.Clipping;
using Yohuke.LiveRecorder.Server.Transcoding;

namespace Yohuke.LiveRecorder.Server.Clipping;

internal sealed class ClipInputResult(ClipInput input, Func<CancellationToken, Task> validate) : IResult
{
    /// <inheritdoc />
    public async Task ExecuteAsync(HttpContext context)
    {
        await validate(context.RequestAborted);
        context.Response.Headers.CacheControl = "private, no-store";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        context.Response.Headers.AcceptRanges = "bytes";
        context.Response.Headers.ETag = input.ETag;
        var rangeText = context.Request.Headers.Range.ToString();
        if (HttpMethods.IsHead(context.Request.Method) && string.IsNullOrEmpty(rangeText))
        {
            context.Response.ContentLength = input.SourceLength;
            return;
        }
        if (!RangeHeaderValue.TryParse(rangeText, out var range) || range.Unit != "bytes" || range.Ranges.Count != 1)
        {
            RejectRange(context);
            return;
        }
        var requested = range.Ranges.Single();
        if (requested.From is not { } start || requested.To is not { } end || start < input.StartOffset
            || end >= input.EndOffset || start > end)
        {
            RejectRange(context);
            return;
        }
        if (context.Request.Headers.IfRange is { Count: > 0 } ifRange && ifRange.ToString() != input.ETag
            || context.Request.Headers.IfMatch is { Count: > 0 } ifMatch && ifMatch.ToString() != input.ETag)
        {
            context.Response.StatusCode = StatusCodes.Status412PreconditionFailed;
            return;
        }
        context.Response.StatusCode = StatusCodes.Status206PartialContent;
        context.Response.ContentType = "application/octet-stream";
        context.Response.ContentLength = end - start + 1;
        context.Response.Headers.ContentRange = FormattableString.Invariant($"bytes {start}-{end}/{input.SourceLength}");
        if (HttpMethods.IsHead(context.Request.Method))
        {
            return;
        }
        await using var file = new FileStream(input.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        file.Position = start;
        await using var guarded = new LeaseValidatedReadStream(file, end + 1, validate);
        await guarded.CopyToAsync(context.Response.Body, context.RequestAborted);
    }

    private void RejectRange(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
        context.Response.Headers.ContentRange = "bytes */" + input.SourceLength.ToString(CultureInfo.InvariantCulture);
    }
}
