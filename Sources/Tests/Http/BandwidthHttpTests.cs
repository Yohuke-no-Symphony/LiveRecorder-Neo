using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>验证真实 HTTP 媒体响应的带宽限制、账号聚合与传输生命周期。</summary>
public sealed class BandwidthHttpTests
{
    /// <summary>管理员的多个登录会话和不同下载入口共同使用一个账号下载限额。</summary>
    [MongoHttpFact]
    public async Task ConcurrentDownloadsAcrossLoginSessionsShareTheUserLimit()
    {
        await using var fixture = await BandwidthHttpFixture.CreateAsync(192 * 1024);
        var secondBrowser = fixture.Server.NewBrowser();
        await secondBrowser.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        await fixture.ConfigureAsync(userLimit: 128 * 1024);

        var elapsed = Stopwatch.StartNew();
        var responses = await Task.WhenAll(
            fixture.Server.Admin.Client.GetByteArrayAsync(fixture.SegmentUrl),
            secondBrowser.Client.GetByteArrayAsync(fixture.OutputUrl));

        Assert.All(responses, bytes => Assert.Equal(fixture.Bytes, bytes));
        Assert.True(elapsed.Elapsed >= TimeSpan.FromSeconds(2.25), $"Two downloads completed in {elapsed.Elapsed}; the account limit was not shared.");
    }

    /// <summary>总带宽限制覆盖所有下载入口、HLS 分片与切片在线播放。</summary>
    [MongoHttpFact]
    public async Task GlobalLimitAppliesToEveryMediaResponse()
    {
        await using var fixture = await BandwidthHttpFixture.CreateAsync(32 * 1024);
        await fixture.ConfigureAsync(smart: true, total: 32 * 1024);

        foreach (var path in new[] { fixture.SegmentUrl, fixture.ClipUrl, fixture.OutputUrl, fixture.PlaybackMediaUrl, fixture.ClipMediaUrl })
        {
            var elapsed = Stopwatch.StartNew();
            using var response = await fixture.Server.Admin.Client.GetAsync(path);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(fixture.Bytes, await response.Content.ReadAsByteArrayAsync());
            Assert.True(elapsed.Elapsed >= TimeSpan.FromSeconds(0.65), $"{path} completed in {elapsed.Elapsed}; the global limit did not pace the body.");
        }
    }

    /// <summary>限速保留 Range、Last-Modified 和无正文条件响应，失败的范围不阻塞后续请求。</summary>
    [MongoHttpFact]
    public async Task RangesAndConditionalResponsesPreserveFileSemantics()
    {
        await using var fixture = await BandwidthHttpFixture.CreateAsync(128);
        await fixture.ConfigureAsync(userLimit: 1024, smart: true, total: 2048);
        using var full = await fixture.Server.Admin.Client.GetAsync(fixture.ClipUrl);
        Assert.Equal(HttpStatusCode.OK, full.StatusCode);
        Assert.Equal("attachment", full.Content.Headers.ContentDisposition!.DispositionType);
        var modified = Assert.IsType<DateTimeOffset>(full.Content.Headers.LastModified);
        Assert.Equal(new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero), modified);

        using (var request = new HttpRequestMessage(HttpMethod.Get, fixture.ClipUrl))
        {
            request.Headers.Range = new RangeHeaderValue(4, 12);
            using var response = await fixture.Server.Admin.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
            Assert.Equal("bytes 4-12/128", response.Content.Headers.ContentRange!.ToString());
            Assert.Equal(9, response.Content.Headers.ContentLength);
            Assert.Equal(fixture.Bytes[4..13], await response.Content.ReadAsByteArrayAsync());
        }

        await fixture.ConfigureAsync(userLimit: 1, smart: true, total: 1);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using (var request = new HttpRequestMessage(HttpMethod.Get, fixture.ClipUrl))
        {
            request.Headers.IfModifiedSince = modified;
            using var response = await fixture.Server.Admin.Client.SendAsync(request, timeout.Token);
            Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
            Assert.Empty(await response.Content.ReadAsByteArrayAsync(timeout.Token));
        }
        using (var request = new HttpRequestMessage(HttpMethod.Get, fixture.ClipUrl))
        {
            request.Headers.IfUnmodifiedSince = modified.AddSeconds(-1);
            using var response = await fixture.Server.Admin.Client.SendAsync(request, timeout.Token);
            Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
            var problem = await response.Content.ReadFromJsonAsync<JsonObject>(timeout.Token);
            Assert.Equal(412, problem!["status"]!.GetValue<int>());
        }
        using (var request = new HttpRequestMessage(HttpMethod.Get, fixture.ClipUrl))
        {
            request.Headers.Range = new RangeHeaderValue(1000, 2000);
            using var response = await fixture.Server.Admin.Client.SendAsync(request, timeout.Token);
            Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, response.StatusCode);
            Assert.Equal("bytes */128", response.Content.Headers.ContentRange!.ToString());
            Assert.Empty(await response.Content.ReadAsByteArrayAsync(timeout.Token));
        }
        await fixture.ConfigureAsync();
        Assert.Equal(fixture.Bytes, await fixture.Server.Admin.Client.GetByteArrayAsync(fixture.ClipUrl));
    }

    /// <summary>提高限制或关闭限速会及时释放已经开始传输的响应。</summary>
    [MongoHttpFact]
    public async Task UpdatingAndDisablingLimitsReleasesActiveResponses()
    {
        await using var fixture = await BandwidthHttpFixture.CreateAsync(256 * 1024);
        await fixture.ConfigureAsync(userLimit: 1024, smart: true, total: 1024);
        await AssertActiveResponseReleasedAsync(fixture, () => fixture.ConfigureAsync(userLimit: 1024 * 1024, smart: true, total: 1024 * 1024));

        await fixture.ConfigureAsync(userLimit: 1024, smart: true, total: 1024);
        await AssertActiveResponseReleasedAsync(fixture, () => fixture.ConfigureAsync());
    }

    /// <summary>客户端取消后其余响应可继续，低于 Kestrel 默认最小数据速率的传输仍跨过五秒宽限期。</summary>
    [MongoHttpFact]
    public async Task CancellationDoesNotBlockSubsequentLowRateResponses()
    {
        await using var fixture = await BandwidthHttpFixture.CreateAsync(128);
        await fixture.ConfigureAsync(userLimit: 16, smart: true, total: 16);
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
        using (var response = await fixture.Server.Admin.Client.GetAsync(fixture.ClipUrl, HttpCompletionOption.ResponseHeadersRead, cancellation.Token))
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellation.Token);
            Assert.Equal(1, await stream.ReadAsync(new byte[1], cancellation.Token));
            await cancellation.CancelAsync();
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(14));
        var elapsed = Stopwatch.StartNew();
        using var continued = await fixture.Server.Admin.Client.GetAsync(fixture.ClipUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

        Assert.Equal(HttpStatusCode.OK, continued.StatusCode);
        Assert.Equal(fixture.Bytes, await continued.Content.ReadAsByteArrayAsync(timeout.Token));
        Assert.True(elapsed.Elapsed >= TimeSpan.FromSeconds(6.5), $"The low-rate response completed in {elapsed.Elapsed} without being paced.");
    }

    private static async Task AssertActiveResponseReleasedAsync(BandwidthHttpFixture fixture, Func<Task> update)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        using var response = await fixture.Server.Admin.Client.GetAsync(fixture.ClipUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        var first = new byte[1];
        Assert.Equal(1, await stream.ReadAsync(first, timeout.Token));
        Assert.Equal(fixture.Bytes[0], first[0]);
        using var output = new MemoryStream();
        var remainder = stream.CopyToAsync(output, timeout.Token);
        await update();
        var elapsed = Stopwatch.StartNew();
        await remainder.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(fixture.Bytes[1..], output.ToArray());
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(2), $"An active response did not adopt the saved bandwidth settings: {elapsed.Elapsed}.");
    }
}
