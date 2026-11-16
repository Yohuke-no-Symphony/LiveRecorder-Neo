using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MongoDB.Driver;
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
        var session = await PlaybackHttpMediaFixture.SeedRecordingAsync(fixture, 95, isClosed: false);
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
        var session = await PlaybackHttpMediaFixture.SeedRecordingAsync(fixture, 4, isClosed: false);
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
        Assert.Equal(playback.DurationMs, playback.EndOffsetMs);
        var cached = await OpenAsync(fixture.Admin, endpoint, new { offsetMs = 0 });
        Assert.Equal(playback.Url, cached.Url);
        var tail = await OpenAsync(fixture.Admin, endpoint, new { offsetMs = playback.DurationMs - 1 });
        Assert.Equal(playback.Url, tail.Url);
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

    /// <summary>已关闭分段只规划连续清单，按需共享片段生成，并在缓存回收后保持原地址可播放。</summary>
    [MongoHttpFact]
    public async Task ClosedRecordingSharesOneLazyPlaylistAndRebuildsEvictedFragments()
    {
        await using var fixture = await PlaybackHttpMediaFixture.CreateAsync(playbackConcurrency: 1);
        var session = await PlaybackHttpMediaFixture.SeedRecordingAsync(fixture, 95);
        var endpoint = $"/api/sessions/{session.Id}/playback";
        var playback = await OpenAsync(fixture.Admin, endpoint, new { offsetMs = 75000, prefetch = true });
        Assert.False(playback.IsGap);
        Assert.Equal(75000d, playback.RequestedOffsetMs);
        Assert.InRange(playback.StartOffsetMs, 0, 100);
        Assert.InRange(playback.EndOffsetMs, 95000, 95200);
        Assert.StartsWith(endpoint + "/", playback.Url);
        Assert.Empty(Directory.EnumerateFiles(fixture.CachePath, "*.m4s", SearchOption.AllDirectories));

        var first = await OpenAsync(fixture.Admin, endpoint, new { offsetMs = 0 });
        Assert.Equal(playback.Url, first.Url);
        Assert.Equal(playback.StartOffsetMs, first.StartOffsetMs);
        Assert.Equal(playback.EndOffsetMs, first.EndOffsetMs);
        var playlist = await ReadPlaylistAsync(fixture.Admin, playback.Url!);
        Assert.Contains("#EXT-X-PLAYLIST-TYPE:VOD", playlist);
        var fragments = playlist.Split('\n').Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#')).ToArray();
        Assert.True(fragments.Length > 1);
        Assert.Equal(fragments.Length, Regex.Matches(playlist, "#EXT-X-MAP:").Count);
        Assert.Empty(Directory.EnumerateFiles(fixture.CachePath, "*.m4s", SearchOption.AllDirectories));

        var baseUri = new Uri(new Uri(fixture.Address, playback.Url!), ".");
        var target = new Uri(baseUri, fragments[fragments.Length / 2]);
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            using var response = await fixture.Admin.Client.GetAsync(target);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await response.Content.ReadAsByteArrayAsync();
        }));
        Assert.NotEmpty(results[0]);
        Assert.All(results, bytes => Assert.Equal(results[0], bytes));
        var cachedFragment = Assert.Single(Directory.EnumerateFiles(fixture.CachePath, "*.m4s", SearchOption.AllDirectories));
        Directory.Delete(Path.GetDirectoryName(cachedFragment)!, recursive: true);
        using (var rebuilt = await fixture.Admin.Client.GetAsync(target))
        {
            Assert.Equal(HttpStatusCode.OK, rebuilt.StatusCode);
            Assert.Equal(results[0], await rebuilt.Content.ReadAsByteArrayAsync());
        }
        Assert.Single(Directory.EnumerateFiles(fixture.CachePath, "*.m4s", SearchOption.AllDirectories));
        Assert.Equal(playlist, await ReadPlaylistAsync(fixture.Admin, playback.Url!));
        await PlaybackHttpMediaFixture.DownloadAndDecodeAsync(fixture, playback);
    }

    /// <summary>窗口文件保留在浏览器私有缓存，内容未变化时返回 304，继续支持范围请求。</summary>
    [MongoHttpFact]
    public async Task WindowAssetsRevalidatePrivateCachesAndPreserveConditionalRanges()
    {
        await using var fixture = await PlaybackHttpMediaFixture.CreateAsync();
        var session = await PlaybackHttpMediaFixture.SeedRecordingAsync(fixture, 12, isClosed: false);
        var playback = await OpenAsync(fixture.Admin, $"/api/sessions/{session.Id}/playback", new { offsetMs = 1000 });
        var resources = ResourceUris(fixture, playback, await ReadPlaylistAsync(fixture.Admin, playback.Url!));
        foreach (var resource in resources)
        {
            using var original = await fixture.Admin.Client.GetAsync(resource);
            Assert.Equal(HttpStatusCode.OK, original.StatusCode);
            var content = await original.Content.ReadAsByteArrayAsync();
            var entityTag = ContentEntityTag(content);
            Assert.Equal(entityTag, original.Headers.ETag?.ToString());
            AssertPrivateRevalidation(original);

            using var unchangedRequest = new HttpRequestMessage(HttpMethod.Get, resource);
            unchangedRequest.Headers.IfNoneMatch.ParseAdd(entityTag);
            using var unchanged = await fixture.Admin.Client.SendAsync(unchangedRequest);
            Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);
            Assert.Empty(await unchanged.Content.ReadAsByteArrayAsync());
            AssertPrivateRevalidation(unchanged);

            using var head = await fixture.Admin.Client.SendAsync(new(HttpMethod.Head, resource));
            Assert.Equal(HttpStatusCode.OK, head.StatusCode);
            Assert.Equal(content.Length, head.Content.Headers.ContentLength);
            Assert.Equal(entityTag, head.Headers.ETag?.ToString());

            using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, resource);
            rangeRequest.Headers.Range = new(4, 31);
            rangeRequest.Headers.IfRange = new(System.Net.Http.Headers.EntityTagHeaderValue.Parse(entityTag));
            using var range = await fixture.Admin.Client.SendAsync(rangeRequest);
            Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode);
            Assert.Equal(content[4..32], await range.Content.ReadAsByteArrayAsync());
            Assert.Equal(entityTag, range.Headers.ETag?.ToString());
        }
    }

    /// <summary>连续资源以真实内容摘要逐次校验私有缓存，并保留 HEAD、Range 和 If-Range 语义。</summary>
    [MongoHttpFact]
    public async Task ContinuousAssetsRevalidatePrivateCachesAndPreserveConditionalRanges()
    {
        await using var fixture = await PlaybackHttpMediaFixture.CreateAsync();
        var session = await PlaybackHttpMediaFixture.SeedRecordingAsync(fixture, 12);
        var playback = await OpenAsync(fixture.Admin, $"/api/sessions/{session.Id}/playback", new { offsetMs = 1000 });
        var resources = ResourceUris(fixture, playback, await ReadPlaylistAsync(fixture.Admin, playback.Url!));
        foreach (var resource in resources)
        {
            using var original = await fixture.Admin.Client.GetAsync(resource);
            Assert.Equal(HttpStatusCode.OK, original.StatusCode);
            var content = await original.Content.ReadAsByteArrayAsync();
            var entityTag = ContentEntityTag(content);
            Assert.Equal(entityTag, original.Headers.ETag?.ToString());
            AssertPrivateRevalidation(original);

            using var unchangedRequest = new HttpRequestMessage(HttpMethod.Get, resource);
            unchangedRequest.Headers.IfNoneMatch.ParseAdd(entityTag);
            using var unchanged = await fixture.Admin.Client.SendAsync(unchangedRequest);
            Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);
            Assert.Empty(await unchanged.Content.ReadAsByteArrayAsync());
            Assert.Equal(entityTag, unchanged.Headers.ETag?.ToString());
            AssertPrivateRevalidation(unchanged);

            using var head = await fixture.Admin.Client.SendAsync(new(HttpMethod.Head, resource));
            Assert.Equal(HttpStatusCode.OK, head.StatusCode);
            Assert.Equal(content.Length, head.Content.Headers.ContentLength);
            Assert.Empty(await head.Content.ReadAsByteArrayAsync());
            Assert.Equal(entityTag, head.Headers.ETag?.ToString());

            using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, resource);
            rangeRequest.Headers.Range = new(4, 31);
            rangeRequest.Headers.IfRange = new(System.Net.Http.Headers.EntityTagHeaderValue.Parse(entityTag));
            using var range = await fixture.Admin.Client.SendAsync(rangeRequest);
            Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode);
            Assert.Equal(content[4..32], await range.Content.ReadAsByteArrayAsync());
            Assert.Equal($"bytes 4-31/{content.Length}", range.Content.Headers.ContentRange?.ToString());
            Assert.Equal(entityTag, range.Headers.ETag?.ToString());

            using var mismatchedRangeRequest = new HttpRequestMessage(HttpMethod.Get, resource);
            mismatchedRangeRequest.Headers.Range = new(4, 31);
            mismatchedRangeRequest.Headers.IfRange = new(new System.Net.Http.Headers.EntityTagHeaderValue("\"different\""));
            using var mismatchedRange = await fixture.Admin.Client.SendAsync(mismatchedRangeRequest);
            Assert.Equal(HttpStatusCode.OK, mismatchedRange.StatusCode);
            Assert.Equal(content, await mismatchedRange.Content.ReadAsByteArrayAsync());

            using var mismatchedRequest = new HttpRequestMessage(HttpMethod.Get, resource);
            mismatchedRequest.Headers.IfNoneMatch.ParseAdd("\"different\"");
            mismatchedRequest.Headers.IfModifiedSince = original.Content.Headers.LastModified;
            using var mismatched = await fixture.Admin.Client.SendAsync(mismatchedRequest);
            Assert.Equal(HttpStatusCode.OK, mismatched.StatusCode);
            Assert.Equal(content, await mismatched.Content.ReadAsByteArrayAsync());
        }
    }

    /// <summary>已有片段补充摘要时不重封装，磁盘回收后重新生成的真实内容仍可通过条件请求复用。</summary>
    [MongoHttpFact]
    public async Task ContinuousAssetsBackfillLegacyHashesAndRevalidateAfterEviction()
    {
        await using var fixture = await PlaybackHttpMediaFixture.CreateAsync();
        var session = await PlaybackHttpMediaFixture.SeedRecordingAsync(fixture, 12);
        var playback = await OpenAsync(fixture.Admin, $"/api/sessions/{session.Id}/playback", new { offsetMs = 1000 });
        var resources = ResourceUris(fixture, playback, await ReadPlaylistAsync(fixture.Admin, playback.Url!));
        using var original = await fixture.Admin.Client.GetAsync(resources[2]);
        var entityTag = ContentEntityTag(await original.Content.ReadAsByteArrayAsync());
        Assert.Equal(entityTag, original.Headers.ETag?.ToString());
        var fragment = Assert.Single(Directory.EnumerateFiles(fixture.CachePath, "chunk_00000.m4s", SearchOption.AllDirectories));
        var directory = Path.GetDirectoryName(fragment)!;
        var marker = Path.Combine(directory, "fragment.json");
        var metadata = JsonNode.Parse(await File.ReadAllTextAsync(marker))!.AsObject();
        Assert.Equal(entityTag.Trim('"'), metadata["mediaContentHash"]!.GetValue<string>());
        var initialization = await File.ReadAllBytesAsync(Path.Combine(directory, "init.mp4"));
        Assert.Equal(ContentEntityTag(initialization).Trim('"'), metadata["initializationContentHash"]!.GetValue<string>());
        metadata.Remove("initializationContentHash");
        metadata.Remove("mediaContentHash");
        await File.WriteAllTextAsync(marker, metadata.ToJsonString());
        var modified = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(fragment, modified);

        using (var legacyRequest = new HttpRequestMessage(HttpMethod.Get, resources[2]))
        {
            legacyRequest.Headers.IfNoneMatch.ParseAdd(entityTag);
            using var legacy = await fixture.Admin.Client.SendAsync(legacyRequest);
            Assert.Equal(HttpStatusCode.NotModified, legacy.StatusCode);
            Assert.Empty(await legacy.Content.ReadAsByteArrayAsync());
        }
        Assert.Equal(modified, File.GetLastWriteTimeUtc(fragment));
        metadata = JsonNode.Parse(await File.ReadAllTextAsync(marker))!.AsObject();
        Assert.Equal(entityTag.Trim('"'), metadata["mediaContentHash"]!.GetValue<string>());
        Assert.Equal(ContentEntityTag(initialization).Trim('"'), metadata["initializationContentHash"]!.GetValue<string>());

        Directory.Delete(directory, recursive: true);
        using var rebuiltRequest = new HttpRequestMessage(HttpMethod.Get, resources[2]);
        rebuiltRequest.Headers.IfNoneMatch.ParseAdd(entityTag);
        using var rebuilt = await fixture.Admin.Client.SendAsync(rebuiltRequest);
        Assert.Equal(HttpStatusCode.NotModified, rebuilt.StatusCode);
        Assert.Empty(await rebuilt.Content.ReadAsByteArrayAsync());
        Assert.Equal(entityTag, rebuilt.Headers.ETag?.ToString());
        Assert.Equal(entityTag, ContentEntityTag(await File.ReadAllBytesAsync(fragment)));
        metadata = JsonNode.Parse(await File.ReadAllTextAsync(marker))!.AsObject();
        Assert.Equal(entityTag.Trim('"'), metadata["mediaContentHash"]!.GetValue<string>());
    }

    /// <summary>连续播放清单、初始化段和片段逐次检查身份，已有地址不能绕过撤销后的观看权限。</summary>
    [MongoHttpFact]
    public async Task ContinuousAssetsRecheckViewingPermissionForEveryResource()
    {
        await using var fixture = await PlaybackHttpMediaFixture.CreateAsync();
        var session = await PlaybackHttpMediaFixture.SeedRecordingAsync(fixture, 12);
        var user = await fixture.CreateUserAsync("continuous-playback-permission@example.test");
        var browser = fixture.NewBrowser();
        await browser.LoginAsync(user.Email, HttpServerFixture.USER_PASSWORD);
        var playback = await OpenAsync(browser, $"/api/sessions/{session.Id}/playback", new { offsetMs = 1000 });
        var playlist = await ReadPlaylistAsync(browser, playback.Url!);
        var resources = ResourceUris(fixture, playback, playlist);
        var anonymous = fixture.NewBrowser();
        foreach (var resource in resources)
        {
            using var denied = await anonymous.Client.GetAsync(resource);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            using var allowed = await browser.Client.GetAsync(resource);
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
            using var conditionalDenied = await GetConditionallyAsync(anonymous, resource);
            Assert.Equal(HttpStatusCode.Unauthorized, conditionalDenied.StatusCode);
            Assert.Null(conditionalDenied.Headers.ETag);
        }
        using (var revoked = await fixture.Admin.SendAsync(HttpMethod.Put, $"/api/admin/users/{user.Id}/permissions",
                   new PermissionSet(PermissionSet.MemberDefaults.Where(id => id != PermissionIds.VIEW_RECORDINGS))))
        {
            Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        }
        foreach (var resource in resources)
        {
            using var denied = await browser.Client.GetAsync(resource);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            using var conditionalDenied = await GetConditionallyAsync(browser, resource);
            Assert.Equal(HttpStatusCode.Forbidden, conditionalDenied.StatusCode);
            Assert.Null(conditionalDenied.Headers.ETag);
        }
    }

    /// <summary>媒体版本变化及场次回收立即使已取得的连续播放资源地址失效。</summary>
    [MongoHttpFact]
    public async Task ContinuousAssetsRejectReplacedSourcesAndTrashedSessions()
    {
        await using var fixture = await PlaybackHttpMediaFixture.CreateAsync();
        var session = await PlaybackHttpMediaFixture.SeedRecordingAsync(fixture, 12);
        var endpoint = $"/api/sessions/{session.Id}/playback";
        var playback = await OpenAsync(fixture.Admin, endpoint, new { offsetMs = 1000 });
        var playlist = await ReadPlaylistAsync(fixture.Admin, playback.Url!);
        var resources = ResourceUris(fixture, playback, playlist);
        foreach (var resource in resources)
        {
            using var allowed = await fixture.Admin.Client.GetAsync(resource);
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }
        await fixture.Context.Collection<RecordingSegment>("segments").UpdateOneAsync(segment => segment.SessionId == session.Id,
            Builders<RecordingSegment>.Update.Inc(segment => segment.Revision, 1));
        await fixture.Context.Collection<BroadcastSession>("sessions").UpdateOneAsync(item => item.Id == session.Id,
            Builders<BroadcastSession>.Update.Inc(item => item.MediaRevision, 1));
        foreach (var resource in resources)
        {
            using var stale = await fixture.Admin.Client.GetAsync(resource);
            Assert.Equal(HttpStatusCode.Gone, stale.StatusCode);
            using var conditionalStale = await GetConditionallyAsync(fixture.Admin, resource);
            Assert.Equal(HttpStatusCode.Gone, conditionalStale.StatusCode);
            Assert.Null(conditionalStale.Headers.ETag);
        }
        var updated = await OpenAsync(fixture.Admin, endpoint, new { offsetMs = 1000 });
        Assert.NotEqual(playback.Url, updated.Url);
        var updatedResources = ResourceUris(fixture, updated, await ReadPlaylistAsync(fixture.Admin, updated.Url!));
        using (var trashed = await fixture.Admin.SendAsync(HttpMethod.Delete, $"/api/sessions/{session.Id}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, trashed.StatusCode);
        }
        foreach (var resource in updatedResources)
        {
            using var unavailable = await fixture.Admin.Client.GetAsync(resource);
            Assert.Equal(HttpStatusCode.NotFound, unavailable.StatusCode);
            using var conditionalUnavailable = await GetConditionallyAsync(fixture.Admin, resource);
            Assert.Equal(HttpStatusCode.NotFound, conditionalUnavailable.StatusCode);
            Assert.Null(conditionalUnavailable.Headers.ETag);
        }
    }

    private static async Task<HttpResponseMessage> GetConditionallyAsync(HttpBrowser browser, Uri resource)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, resource);
        request.Headers.IfNoneMatch.ParseAdd("*");
        return await browser.Client.SendAsync(request);
    }

    private static async Task<string> ReadPlaylistAsync(HttpBrowser browser, string url)
    {
        using var response = await browser.Client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/vnd.apple.mpegurl", response.Content.Headers.ContentType?.MediaType);
        var playlist = await response.Content.ReadAsStringAsync();
        Assert.StartsWith("#EXTM3U", playlist);
        Assert.Contains("#EXT-X-ENDLIST", playlist);
        return playlist;
    }

    private static string ContentEntityTag(byte[] content) => "\"" + Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant() + "\"";

    private static void AssertPrivateRevalidation(HttpResponseMessage response)
    {
        Assert.NotNull(response.Headers.CacheControl);
        Assert.True(response.Headers.CacheControl.Private);
        Assert.True(response.Headers.CacheControl.NoCache);
        Assert.True(response.Headers.CacheControl.MustRevalidate);
        Assert.False(response.Headers.CacheControl.NoStore);
        Assert.Null(response.Headers.CacheControl.MaxAge);
    }

    private static Uri[] ResourceUris(HttpServerFixture fixture, PlaybackResponse playback, string playlist)
    {
        var manifest = new Uri(fixture.Address, playback.Url!);
        var initialization = Regex.Match(playlist, "#EXT-X-MAP:URI=\"([^\"]+)\"");
        Assert.True(initialization.Success);
        var fragment = playlist.Split('\n').Select(line => line.Trim()).First(line => line.Length > 0 && !line.StartsWith('#'));
        return [manifest, new Uri(manifest, initialization.Groups[1].Value), new Uri(manifest, fragment)];
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
