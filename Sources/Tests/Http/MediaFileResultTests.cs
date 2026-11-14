using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using Yohuke.LiveRecorder.Server.Bandwidth;
using Xunit;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>验证媒体结果在首次调度失败和无正文响应时保留正确的 HTTP 生命周期。</summary>
public sealed class MediaFileResultTests
{
    /// <summary>首次取得配置失败时清除文件响应头，以便外层错误处理中间件输出完整问题响应。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitialSchedulerFailureClearsFileHeadersBeforePropagating(bool revalidateCache)
    {
        var path = Path.Combine(Path.GetTempPath(), "live-recorder-bandwidth-result-" + Guid.NewGuid().ToString("N") + ".mp4");
        try
        {
            await File.WriteAllBytesAsync(path, new byte[512]);
            var scheduler = new FailingMediaBandwidthScheduler();
            await using var services = Services(scheduler);
            var http = Context(services);
            http.Request.Headers.Range = "bytes=4-127";

            await Assert.ThrowsAsync<IOException>(() => new MediaFileResult(path, "video/mp4", MediaTrafficKind.Download, "test.mp4",
                contentHash: revalidateCache ? new string('a', 64) : null).ExecuteAsync(http));

            Assert.Equal(1, scheduler.Calls);
            Assert.False(http.Response.HasStarted);
            Assert.Null(http.Response.ContentLength);
            Assert.False(http.Response.Headers.ContainsKey(HeaderNames.ContentDisposition));
            Assert.False(http.Response.Headers.ContainsKey(HeaderNames.ContentRange));
            Assert.False(http.Response.Headers.ContainsKey(HeaderNames.AcceptRanges));
            Assert.False(http.Response.Headers.ContainsKey(HeaderNames.LastModified));
            Assert.False(http.Response.Headers.ContainsKey(HeaderNames.ETag));
            Assert.Equal("private, no-store", http.Response.Headers.CacheControl);
            Assert.Equal("no", http.Response.Headers["X-Accel-Buffering"]);
            Assert.Equal(0, http.Response.Body.Length);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>HEAD、缓存条件和无效范围没有正文，不读取配置也不注册带宽传输。</summary>
    [Theory]
    [InlineData("HEAD", "", StatusCodes.Status200OK)]
    [InlineData("GET", "modified", StatusCodes.Status304NotModified)]
    [InlineData("GET", "unmodified", StatusCodes.Status412PreconditionFailed)]
    [InlineData("GET", "range", StatusCodes.Status416RangeNotSatisfiable)]
    [InlineData("GET", "matched", StatusCodes.Status304NotModified)]
    [InlineData("HEAD", "matched", StatusCodes.Status304NotModified)]
    [InlineData("GET", "mismatch", StatusCodes.Status412PreconditionFailed)]
    public async Task ResponsesWithoutBodiesNeverOpenBandwidthTransfers(string method, string condition, int expectedStatus)
    {
        var path = Path.Combine(Path.GetTempPath(), "live-recorder-bandwidth-result-" + Guid.NewGuid().ToString("N") + ".mp4");
        try
        {
            await File.WriteAllBytesAsync(path, new byte[512]);
            var modified = new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero);
            File.SetLastWriteTimeUtc(path, modified.UtcDateTime);
            var scheduler = new FailingMediaBandwidthScheduler();
            await using var services = Services(scheduler);
            var http = Context(services);
            http.Request.Method = method;
            var contentHash = condition is "matched" or "mismatch" ? new string('a', 64) : null;
            switch (condition)
            {
                case "modified":
                {
                    http.Request.Headers.IfModifiedSince = modified.ToString("R");
                    break;
                }
                case "unmodified":
                {
                    http.Request.Headers.IfUnmodifiedSince = modified.AddSeconds(-1).ToString("R");
                    break;
                }
                case "range":
                {
                    http.Request.Headers.Range = "bytes=1000-2000";
                    break;
                }
                case "matched":
                {
                    http.Request.Headers.IfNoneMatch = "\"" + contentHash + "\"";
                    break;
                }
                case "mismatch":
                {
                    http.Request.Headers.IfMatch = "\"different\"";
                    break;
                }
            }

            await new MediaFileResult(path, "video/mp4", MediaTrafficKind.Download, "test.mp4", contentHash).ExecuteAsync(http);

            Assert.Equal(expectedStatus, http.Response.StatusCode);
            Assert.Equal(0, scheduler.Calls);
            Assert.Equal(0, http.Response.Body.Length);
            Assert.Equal(contentHash is null ? "private, no-store" : "private, no-cache, must-revalidate", http.Response.Headers.CacheControl);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static ServiceProvider Services(FailingMediaBandwidthScheduler scheduler) => new ServiceCollection()
        .AddLogging()
        .AddSingleton<IMediaBandwidthScheduler>(scheduler)
        .BuildServiceProvider();

    private static DefaultHttpContext Context(IServiceProvider services)
    {
        var http = new DefaultHttpContext
        {
            RequestServices = services,
            User = new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "bandwidth-user")], "Test"))
        };
        http.Request.Method = "GET";
        http.Response.Body = new MemoryStream();
        return http;
    }
}
