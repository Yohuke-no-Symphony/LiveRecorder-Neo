using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Yohuke.LiveRecorder.Infrastructure.Clipping;
using Yohuke.LiveRecorder.Server.Bandwidth;
using Yohuke.LiveRecorder.Server.Clipping;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>Verifies that clipping HTTP inputs expose only explicitly assigned finite byte ranges.</summary>
public sealed class ClipInputResultTests
{
    /// <summary>Missing, open, suffix, multiple and out-of-plan ranges never emit source bytes.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("bytes=200-")]
    [InlineData("bytes=-100")]
    [InlineData("bytes=200-220,250-270")]
    [InlineData("bytes=199-399")]
    [InlineData("bytes=200-400")]
    [InlineData("bytes=0-999")]
    [InlineData("items=200-399")]
    public async Task RejectsRangesOutsideAssignmentWithoutReadingSource(string? range)
    {
        using var body = new MemoryStream();
        var http = new DefaultHttpContext();
        http.Request.Method = "GET";
        http.Response.Body = body;
        if (range is not null)
        {
            http.Request.Headers.Range = range;
        }
        await new ClipInputResult(new("missing-source.flv", "\"version\"", 1000, 200, 400), _ => Task.CompletedTask).ExecuteAsync(http);
        Assert.Equal(StatusCodes.Status416RangeNotSatisfiable, http.Response.StatusCode);
        Assert.Equal("bytes */1000", http.Response.Headers.ContentRange);
        Assert.Empty(body.ToArray());
    }

    /// <summary>Finite subranges preserve absolute source offsets and bypass the website bandwidth scheduler.</summary>
    [Theory]
    [InlineData("GET", 200, 399)]
    [InlineData("GET", 250, 300)]
    [InlineData("HEAD", 200, 399)]
    public async Task AssignedSubrangesHaveExactBytesAndVersion(string method, int start, int end)
    {
        var path = Path.Combine(Path.GetTempPath(), "live-recorder-clip-input-" + Guid.NewGuid().ToString("N"));
        try
        {
            var bytes = Enumerable.Range(0, 1200).Select(value => (byte)value).ToArray();
            await File.WriteAllBytesAsync(path, bytes);
            var scheduler = new FailingMediaBandwidthScheduler();
            await using var services = new ServiceCollection().AddSingleton<IMediaBandwidthScheduler>(scheduler).BuildServiceProvider();
            using var body = new MemoryStream();
            var http = new DefaultHttpContext { RequestServices = services };
            http.Request.Method = method;
            http.Request.Headers.Range = $"bytes={start}-{end}";
            http.Request.Headers.IfRange = "\"version\"";
            http.Response.Body = body;
            await new ClipInputResult(new(path, "\"version\"", 1000, 200, 400), _ => Task.CompletedTask).ExecuteAsync(http);
            Assert.Equal(StatusCodes.Status206PartialContent, http.Response.StatusCode);
            Assert.Equal($"bytes {start}-{end}/1000", http.Response.Headers.ContentRange);
            Assert.Equal(end - start + 1, http.Response.ContentLength);
            Assert.Equal("\"version\"", http.Response.Headers.ETag);
            Assert.Equal("private, no-store", http.Response.Headers.CacheControl);
            Assert.Equal(method == "HEAD" ? [] : bytes[start..(end + 1)], body.ToArray());
            Assert.Equal(0, scheduler.Calls);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>A failed conditional range never falls back to a full HTTP 200 response.</summary>
    [Fact]
    public async Task StaleIfRangeFailsWithoutSendingWholeSource()
    {
        using var body = new MemoryStream();
        var http = new DefaultHttpContext();
        http.Request.Method = "GET";
        http.Request.Headers.Range = "bytes=200-399";
        http.Request.Headers.IfRange = "\"previous-version\"";
        http.Response.Body = body;
        await new ClipInputResult(new("missing-source.flv", "\"version\"", 1000, 200, 400), _ => Task.CompletedTask).ExecuteAsync(http);
        Assert.Equal(StatusCodes.Status412PreconditionFailed, http.Response.StatusCode);
        Assert.Empty(body.ToArray());
    }

    /// <summary>Lease validation runs before even a HEAD response can expose source metadata.</summary>
    [Fact]
    public async Task RevokedLeaseCannotReadInput()
    {
        using var body = new MemoryStream();
        var http = new DefaultHttpContext();
        http.Request.Method = "HEAD";
        http.Response.Body = body;
        await Assert.ThrowsAsync<InvalidDataException>(() => new ClipInputResult(new("missing", "\"version\"", 1000, 200, 400),
            _ => Task.FromException(new InvalidDataException("Revoked lease"))).ExecuteAsync(http));
        Assert.Empty(body.ToArray());
    }
}
