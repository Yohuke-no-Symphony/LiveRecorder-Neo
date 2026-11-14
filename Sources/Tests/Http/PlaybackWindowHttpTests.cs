using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Playback;
using Xunit;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>验证真实播放窗口生成、预取及观看权限的 HTTP 契约。</summary>
public sealed class PlaybackWindowHttpTests
{
    /// <summary>冷跳转只生成目标附近的短窗口，预取与前台命中共享可实际解码的缓存。</summary>
    [MongoHttpFact]
    public async Task ColdAndPrefetchedWindowsCoverTheTargetAndServeDecodableMedia()
    {
        await using var fixture = await PlaybackHttpMediaFixture.CreateAsync();
        var session = await PlaybackHttpMediaFixture.SeedRecordingAsync(fixture, 95);
        var endpoint = $"/api/sessions/{session.Id}/playback";
        var first = await OpenAsync(fixture.Admin, endpoint, new { offsetMs = 45000 });
        Assert.False(first.IsGap);
        Assert.Equal(45000d, first.RequestedOffsetMs);
        Assert.InRange(first.StartOffsetMs, 29000, 30000);
        Assert.InRange(first.EndOffsetMs, 60000, 61000);
        Assert.True(first.StartOffsetMs <= 45000 && first.EndOffsetMs > 45000);
        await PlaybackHttpMediaFixture.DownloadAndDecodeAsync(fixture, first);

        var cached = await OpenAsync(fixture.Admin, endpoint, new { offsetMs = 47000, prefetch = false });
        Assert.Equal(first.Url, cached.Url);
        Assert.Equal(first.StartOffsetMs, cached.StartOffsetMs);
        Assert.Equal(first.EndOffsetMs, cached.EndOffsetMs);
        var next = await OpenAsync(fixture.Admin, endpoint, new { offsetMs = first.EndOffsetMs, prefetch = true });
        Assert.NotEqual(first.Url, next.Url);
        Assert.True(next.StartOffsetMs <= first.EndOffsetMs && next.EndOffsetMs > first.EndOffsetMs);
        var promoted = await OpenAsync(fixture.Admin, endpoint, new { offsetMs = 75000 });
        Assert.Equal(next.Url, promoted.Url);
        await PlaybackHttpMediaFixture.DownloadAndDecodeAsync(fixture, promoted);
        Assert.Equal(2, Directory.EnumerateFiles(Path.Combine(fixture.CachePath, "playback"), "window.json", SearchOption.AllDirectories).Count());
    }

    /// <summary>单播放名额时预取立即跳过，但普通请求仍能生成和读取同一目标。</summary>
    [MongoHttpFact]
    public async Task SinglePlaybackSlotSkipsPrefetchWithoutPreventingForegroundPlayback()
    {
        await using var fixture = await PlaybackHttpMediaFixture.CreateAsync(playbackConcurrency: 1);
        var session = await PlaybackHttpMediaFixture.SeedRecordingAsync(fixture, 4);
        var endpoint = $"/api/sessions/{session.Id}/playback";
        using (var skipped = await fixture.Admin.SendAsync(HttpMethod.Post, endpoint, new { offsetMs = 1000, prefetch = true }))
        {
            Assert.Equal(HttpStatusCode.NoContent, skipped.StatusCode);
            Assert.Empty(await skipped.Content.ReadAsByteArrayAsync());
        }
        var playback = await OpenAsync(fixture.Admin, endpoint, new { offsetMs = 1000 });
        Assert.True(playback.StartOffsetMs <= 1000 && playback.EndOffsetMs > 1000);
        var cached = await OpenAsync(fixture.Admin, endpoint, new { offsetMs = 1000, prefetch = true });
        Assert.Equal(playback.Url, cached.Url);
        await PlaybackHttpMediaFixture.DownloadAndDecodeAsync(fixture, playback);
    }

    /// <summary>音频编码造成首个视频关键帧稍晚于零点时，首次打开仍能播放并复用缓存。</summary>
    [MongoHttpFact]
    public async Task InitialPlaybackAtZeroAcceptsTheEncoderTimestampOffset()
    {
        await using var fixture = await PlaybackHttpMediaFixture.CreateAsync();
        var session = await PlaybackHttpMediaFixture.SeedRecordingAsync(fixture, 4);
        var endpoint = $"/api/sessions/{session.Id}/playback";
        var playback = await OpenAsync(fixture.Admin, endpoint, new { offsetMs = 0 });
        Assert.Equal(0d, playback.RequestedOffsetMs);
        Assert.InRange(playback.StartOffsetMs, 0, 100);
        Assert.True(playback.EndOffsetMs > 1000);
        var cached = await OpenAsync(fixture.Admin, endpoint, new { offsetMs = 0 });
        Assert.Equal(playback.Url, cached.Url);
        await PlaybackHttpMediaFixture.DownloadAndDecodeAsync(fixture, playback);
    }

    /// <summary>跳过预取不改变前台的真实空档和媒体尚未提交响应。</summary>
    [MongoHttpFact]
    public async Task ForegroundPreservesGapAndUncommittedMediaSemantics()
    {
        await using var fixture = await PlaybackHttpMediaFixture.CreateAsync();
        var session = await SeedGapSessionAsync(fixture);
        var endpoint = $"/api/sessions/{session.Id}/playback";
        var gap = await OpenAsync(fixture.Admin, endpoint, new { offsetMs = 7500 });
        Assert.True(gap.IsGap);
        Assert.Null(gap.Url);
        Assert.Equal(5000d, gap.StartOffsetMs);
        Assert.Equal(10000d, gap.EndOffsetMs);
        Assert.Equal(10000d, gap.GapEndMs);
        using (var pending = await fixture.Admin.SendAsync(HttpMethod.Post, endpoint, new { offsetMs = 1000 }))
        {
            Assert.Equal((HttpStatusCode)425, pending.StatusCode);
            var problem = await pending.Content.ReadFromJsonAsync<JsonObject>();
            Assert.Equal("PLAYBACK_MEDIA_NOT_COMMITTED", problem!["code"]!.GetValue<string>());
        }
        foreach (var offset in new[] { 1000, 7500 })
        {
            using var skipped = await fixture.Admin.SendAsync(HttpMethod.Post, endpoint, new { offsetMs = offset, prefetch = true });
            Assert.Equal(HttpStatusCode.NoContent, skipped.StatusCode);
        }
    }

    /// <summary>预取请求与普通播放共用观看权限，已登录用户撤权后立即失去两种入口。</summary>
    [MongoHttpFact]
    public async Task PrefetchCannotBypassRevokedViewingPermission()
    {
        await using var fixture = await PlaybackHttpMediaFixture.CreateAsync();
        var session = await SeedGapSessionAsync(fixture);
        var user = await fixture.CreateUserAsync("playback-prefetch-permission@example.test");
        var browser = fixture.NewBrowser();
        await browser.LoginAsync(user.Email, HttpServerFixture.USER_PASSWORD);
        var endpoint = $"/api/sessions/{session.Id}/playback";
        using (var allowed = await browser.SendAsync(HttpMethod.Post, endpoint, new { offsetMs = 7500, prefetch = true }))
        {
            Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);
        }
        using (var revoked = await fixture.Admin.SendAsync(HttpMethod.Put, $"/api/admin/users/{user.Id}/permissions",
                   new PermissionSet(PermissionSet.MemberDefaults.Where(id => id != PermissionIds.VIEW_RECORDINGS))))
        {
            Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        }
        foreach (var prefetch in new[] { false, true })
        {
            using var denied = await browser.SendAsync(HttpMethod.Post, endpoint, new { offsetMs = 7500, prefetch });
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
    }

    private static async Task<PlaybackResponse> OpenAsync(HttpBrowser browser, string endpoint, object request)
    {
        using var response = await browser.SendAsync(HttpMethod.Post, endpoint, request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<PlaybackResponse>() ?? throw new InvalidDataException("Missing playback window.");
    }

    private static async Task<BroadcastSession> SeedGapSessionAsync(HttpServerFixture fixture)
    {
        var started = DateTime.UtcNow.AddMinutes(-1);
        var session = new BroadcastSession
        {
            Title = "HTTP 播放边界回归",
            DirectoryPath = Path.Combine("playback-http", Guid.NewGuid().ToString("N")),
            StartedAtUtc = started,
            EndedAtUtc = started.AddSeconds(15),
            LastMediaAtUtc = started.AddSeconds(15),
            State = "Completed"
        };
        await fixture.Context.Collection<BroadcastSession>("sessions").InsertOneAsync(session);
        await fixture.Context.Collection<RecordingSegment>("segments").InsertManyAsync(
        [
            new RecordingSegment { SessionId = session.Id, RelativePath = "uncommitted.flv", DurationMs = 5000, CommittedBytes = 0, IsClosed = false },
            new RecordingSegment { SessionId = session.Id, RelativePath = "second.flv", StartOffsetMs = 10000, DurationMs = 5000, CommittedBytes = 64, IsClosed = true }
        ]);
        return session;
    }
}
