using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Yohuke.LiveRecorder.Infrastructure.Transcoding;
using Yohuke.LiveRecorder.Server.Bandwidth;
using Yohuke.LiveRecorder.Server.Transcoding;
using Xunit;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>验证 Worker 内部源文件传输独立于网站带宽调度，并受源文件范围及租约约束。</summary>
public sealed class WorkerInputResultTests
{
    /// <summary>完整下载、范围下载与 HEAD 都不访问网站带宽调度器。</summary>
    [Theory]
    [InlineData("GET", false)]
    [InlineData("GET", true)]
    [InlineData("HEAD", false)]
    public async Task InternalTransfersBypassWebsiteBandwidthScheduler(string method, bool range)
    {
        var path = Path.Combine(Path.GetTempPath(), "live-recorder-worker-input-" + Guid.NewGuid().ToString("N"));
        try
        {
            var bytes = Enumerable.Range(0, 1024).Select(value => (byte)value).ToArray();
            await File.WriteAllBytesAsync(path, bytes);
            var scheduler = new FailingMediaBandwidthScheduler();
            await using var services = new ServiceCollection().AddLogging().AddSingleton<IMediaBandwidthScheduler>(scheduler)
                .BuildServiceProvider();
            using var body = new MemoryStream();
            var http = new DefaultHttpContext { RequestServices = services };
            http.Request.Method = method;
            http.Response.Body = body;
            if (range)
            {
                http.Request.Headers.Range = "bytes=4-127";
            }
            var validations = 0;
            var input = new TranscodeInput(path, "\"source-version\"", 512);
            await new WorkerInputResult(input, _ =>
            {
                validations++;
                return Task.CompletedTask;
            }).ExecuteAsync(http);

            Assert.Equal(0, scheduler.Calls);
            Assert.Equal(range ? StatusCodes.Status206PartialContent : StatusCodes.Status200OK, http.Response.StatusCode);
            Assert.Equal(range ? 124 : 512, http.Response.ContentLength);
            Assert.Equal(input.ETag, http.Response.Headers.ETag);
            Assert.Equal("private, no-store", http.Response.Headers.CacheControl);
            Assert.Equal("no", http.Response.Headers["X-Accel-Buffering"]);
            if (method == "HEAD")
            {
                Assert.Equal(0, validations);
                Assert.Empty(body.ToArray());
            }
            else
            {
                Assert.Equal(1, validations);
                Assert.Equal(range ? bytes[4..128] : bytes[..512], body.ToArray());
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>源文件正文在读取前必须通过租约复验。</summary>
    [Fact]
    public async Task InternalTransferRequiresValidLeaseBeforeReadingBody()
    {
        var path = Path.Combine(Path.GetTempPath(), "live-recorder-worker-input-" + Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllBytesAsync(path, new byte[512]);
            await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
            using var body = new MemoryStream();
            var http = new DefaultHttpContext { RequestServices = services };
            http.Request.Method = "GET";
            http.Response.Body = body;
            var input = new TranscodeInput(path, "\"source-version\"", 512);

            await Assert.ThrowsAsync<InvalidDataException>(() => new WorkerInputResult(input,
                _ => Task.FromException(new InvalidDataException("The job lease was revoked."))).ExecuteAsync(http));

            Assert.Empty(body.ToArray());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
