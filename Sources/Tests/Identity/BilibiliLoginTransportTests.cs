using System.IO.Compression;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Yohuke.LiveRecorder.Infrastructure.Auth;
using Xunit;

namespace Yohuke.LiveRecorder.Identity.Tests;

/// <summary>验证采集账号使用的 HTTP 客户端可以读取平台压缩后的挑战页。</summary>
public sealed class BilibiliLoginTransportTests
{
    [Fact]
    public async Task NamedClientReadsGzipChallengeAsHtml()
    {
        const string CHALLENGE = "<html><div id='1-name'>challenge</div></html>";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapGet("/correspond/1/test", async (HttpContext context) =>
        {
            var response = context.Response;
            response.ContentType = "text/html; charset=utf-8";
            response.Headers.ContentEncoding = "gzip";
            await using var gzip = new GZipStream(response.Body, CompressionLevel.Fastest, leaveOpen: true);
            await gzip.WriteAsync(Encoding.UTF8.GetBytes(CHALLENGE), context.RequestAborted);
        });
        await app.StartAsync(timeout.Token);

        using var services = new ServiceCollection().AddLogging().AddIdentityServices().BuildServiceProvider();
        using var client = services.GetRequiredService<IHttpClientFactory>().CreateClient("bilibili-login");
        var html = await client.GetStringAsync(new Uri(new Uri(app.Urls.Single()), "/correspond/1/test"), timeout.Token);
        Assert.Equal(CHALLENGE, html);
    }
}
