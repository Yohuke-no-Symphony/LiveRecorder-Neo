using System.Net;
using System.Net.Http.Json;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Driver;
using Xunit;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Files;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>验证完整事件快照的权限、浏览器条件缓存及直播追加协议。</summary>
public sealed class PlaybackEventSnapshotHttpTests
{
    /// <summary>完整快照使用私有条件缓存，省略原始协议而保留展示负载及当前规则匹配结果。</summary>
    [MongoHttpFact]
    public async Task SnapshotIsPrivateConditionallyCacheableAndOmitsRawProtocol()
    {
        await using var fixture = await CreateFixtureAsync();
        var session = await SeedSessionAsync(fixture);
        await WriteAsync(fixture, session,
            new() { OffsetMs = 1000, Text = "【同传】", RawJson = "不应交付浏览器的完整协议", Payload = new() { ["replyUserName"] = "原发言者" } },
            new() { OffsetMs = 2000, Text = "普通弹幕" });
        var path = PathFor(session);
        using var initial = await fixture.Admin.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        var snapshot = (await initial.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.True(snapshot["reset"]!.GetValue<bool>());
        Assert.False(snapshot["isLive"]!.GetValue<bool>());
        var items = snapshot["items"]!.AsArray();
        Assert.Equal(2, items.Count);
        Assert.All(items, item => Assert.False(item!.AsObject().ContainsKey("rawJson")));
        Assert.Equal("原发言者", items[0]!["payload"]!["replyUserName"]!.GetValue<string>());
        Assert.Equal(1, Assert.Single(snapshot["translationSequences"]!.AsArray())!.GetValue<long>());
        Assert.True(initial.Headers.CacheControl!.Private);
        Assert.True(initial.Headers.CacheControl.NoCache);
        Assert.True(initial.Headers.CacheControl.MustRevalidate);
        Assert.Contains("Accept-Language", initial.Headers.Vary);
        Assert.NotNull(initial.Headers.ETag);

        using var conditional = new HttpRequestMessage(HttpMethod.Get, path);
        conditional.Headers.IfNoneMatch.Add(initial.Headers.ETag);
        using var unchanged = await fixture.Admin.Client.SendAsync(conditional);
        Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);
        Assert.Empty(await unchanged.Content.ReadAsByteArrayAsync());
        Assert.Equal(initial.Headers.ETag, unchanged.Headers.ETag);
        var cursor = snapshot["cursor"]!.GetValue<string>();
        var delta = (await fixture.Admin.Client.GetFromJsonAsync<SessionEventSnapshot>(path + "?cursor=" + Uri.EscapeDataString(cursor)))!;
        Assert.False(delta.Reset);
        Assert.Empty(delta.Items);
        Assert.Equal(snapshot["version"]!.GetValue<string>(), delta.Version);
    }

    /// <summary>直播全量包括已提交未来偏移，后续仅追加新事件，结束时重置并应用最终播放边界。</summary>
    [MongoHttpFact]
    public async Task LiveCursorDeliversOnlyAppendsAndCompletionResetsTheBoundary()
    {
        await using var fixture = await CreateFixtureAsync();
        var session = await SeedSessionAsync(fixture, true);
        await WriteAsync(fixture, session, new RecordedEventEnvelope { OffsetMs = 2_000_000, Text = "【未来偏移】" });
        var first = (await fixture.Admin.Client.GetFromJsonAsync<SessionEventSnapshot>(PathFor(session)))!;
        Assert.True(first.IsLive);
        Assert.Equal(2_000_000, Assert.Single(first.Items).OffsetMs);
        await WriteAsync(fixture, session, new RecordedEventEnvelope { OffsetMs = 500, Text = "【后到的早期事件】" });

        var delta = (await fixture.Admin.Client.GetFromJsonAsync<SessionEventSnapshot>(PathFor(session) + "?cursor=" + Uri.EscapeDataString(first.Cursor)))!;

        Assert.False(delta.Reset);
        Assert.Equal(first.Version, delta.Version);
        Assert.Equal(2, Assert.Single(delta.Items).Sequence);
        Assert.Equal([2L], delta.TranslationSequences);
        await fixture.Context.Collection<BroadcastSession>("sessions").UpdateOneAsync(value => value.Id == session.Id,
            Builders<BroadcastSession>.Update.Set(value => value.EndedAtUtc, session.StartedAtUtc.AddSeconds(10)).Set(value => value.State, "Completed"));
        var completed = (await fixture.Admin.Client.GetFromJsonAsync<SessionEventSnapshot>(PathFor(session) + "?cursor=" + Uri.EscapeDataString(delta.Cursor)))!;
        Assert.True(completed.Reset);
        Assert.False(completed.IsLive);
        Assert.NotEqual(delta.Version, completed.Version);
        Assert.Equal(2, Assert.Single(completed.Items).Sequence);
        Assert.Equal([2L], completed.TranslationSequences);
    }

    /// <summary>全局同传规则变化使旧快照失效，并使用当前 .NET 规则重新标识完整事件。</summary>
    [MongoHttpFact]
    public async Task CurrentTranslationRulesInvalidateExistingCursors()
    {
        await using var fixture = await CreateFixtureAsync();
        var session = await SeedSessionAsync(fixture);
        await WriteAsync(fixture, session, new() { OffsetMs = 1000, Text = "【原规则】" }, new() { OffsetMs = 2000, Text = "JP: 新规则" });
        var first = (await fixture.Admin.Client.GetFromJsonAsync<SessionEventSnapshot>(PathFor(session)))!;
        Assert.Equal([1L], first.TranslationSequences);
        var settings = (await fixture.Admin.Client.GetFromJsonAsync<JsonObject>("/api/settings"))!;
        settings["translationDanmuPattern"] = "(?i)^jp:";
        using var updated = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/settings", settings);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        var next = (await fixture.Admin.Client.GetFromJsonAsync<SessionEventSnapshot>(PathFor(session) + "?cursor=" + Uri.EscapeDataString(first.Cursor)))!;

        Assert.True(next.Reset);
        Assert.Equal(2, next.Items.Count);
        Assert.Equal([2L], next.TranslationSequences);
        Assert.NotEqual(first.Version, next.Version);
    }

    /// <summary>权限撤销、退出登录或场次被隐藏后，持有旧 ETag 也不得通过 304 恢复访问。</summary>
    [MongoHttpFact]
    public async Task ConditionalRequestsCannotBypassCurrentAccessChecks()
    {
        await using var fixture = await CreateFixtureAsync();
        var session = await SeedSessionAsync(fixture);
        await WriteAsync(fixture, session, new RecordedEventEnvelope { OffsetMs = 1000, Text = "已有弹幕" });
        var user = await fixture.CreateUserAsync("snapshot-viewer@example.test");
        var browser = fixture.NewBrowser();
        await browser.LoginAsync(user.Email, HttpServerFixture.USER_PASSWORD);
        using var initial = await browser.Client.GetAsync(PathFor(session));
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        var entityTag = initial.Headers.ETag!;
        using var anonymousRequest = new HttpRequestMessage(HttpMethod.Get, PathFor(session));
        anonymousRequest.Headers.IfNoneMatch.Add(entityTag);
        using var anonymous = await fixture.NewBrowser().Client.SendAsync(anonymousRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var updated = await fixture.Admin.SendAsync(HttpMethod.Put, $"/api/admin/users/{user.Id}/permissions",
            new PermissionSet(PermissionSet.MemberDefaults.Where(id => id != PermissionIds.VIEW_RECORDINGS)));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        using var deniedRequest = new HttpRequestMessage(HttpMethod.Get, PathFor(session));
        deniedRequest.Headers.IfNoneMatch.Add(entityTag);
        using var denied = await browser.Client.SendAsync(deniedRequest);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        await fixture.Context.Collection<BroadcastSession>("sessions").UpdateOneAsync(value => value.Id == session.Id,
            Builders<BroadcastSession>.Update.Set(value => value.IsHidden, true));
        using var hiddenRequest = new HttpRequestMessage(HttpMethod.Get, PathFor(session));
        hiddenRequest.Headers.IfNoneMatch.Add(entityTag);
        using var hidden = await fixture.Admin.Client.SendAsync(hiddenRequest);
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.Null(hidden.Headers.ETag);
    }

    /// <summary>同传规则超时不阻断完整弹幕，错误跨空增量和新增事件持续保留，修正规则后恢复。</summary>
    [MongoHttpFact]
    public async Task TranslationTimeoutIsIsolatedAndPersistsUntilRulesRecover()
    {
        await using var fixture = await CreateFixtureAsync();
        var session = await SeedSessionAsync(fixture);
        await WriteAsync(fixture, session,
            new() { OffsetMs = 1000, Text = "aaaa" },
            new() { OffsetMs = 2000, Text = new string('a', 100000) + "!" },
            new() { OffsetMs = 3000, Text = "普通弹幕" });
        using var configured = await fixture.Admin.SendAsync(HttpMethod.Put, $"/api/sessions/{session.Id}/configuration",
            new { translationDanmuPatternOverride = "(a+)+$", expectedRevision = 1 });
        Assert.Equal(HttpStatusCode.OK, configured.StatusCode);
        using var initialRequest = new HttpRequestMessage(HttpMethod.Get, PathFor(session));
        initialRequest.Headers.AcceptLanguage.ParseAdd("en-US");
        using var initial = await fixture.Admin.Client.SendAsync(initialRequest);
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        var first = (await initial.Content.ReadFromJsonAsync<SessionEventSnapshot>())!;
        Assert.Equal(3, first.Items.Count);
        Assert.Empty(first.TranslationSequences);
        Assert.Equal("The translation regular expression timed out. Simplify the pattern and try again.", first.TranslationError);
        using var emptyRequest = new HttpRequestMessage(HttpMethod.Get, PathFor(session) + "?cursor=" + Uri.EscapeDataString(first.Cursor));
        emptyRequest.Headers.AcceptLanguage.ParseAdd("en-US");
        using var emptyResponse = await fixture.Admin.Client.SendAsync(emptyRequest);
        var empty = (await emptyResponse.Content.ReadFromJsonAsync<SessionEventSnapshot>())!;
        Assert.False(empty.Reset);
        Assert.Empty(empty.Items);
        Assert.Equal(first.TranslationError, empty.TranslationError);
        await WriteAsync(fixture, session, new RecordedEventEnvelope { OffsetMs = 4000, Text = "aaaa" });
        using var deltaRequest = new HttpRequestMessage(HttpMethod.Get, PathFor(session) + "?cursor=" + Uri.EscapeDataString(empty.Cursor));
        deltaRequest.Headers.AcceptLanguage.ParseAdd("en-US");
        using var deltaResponse = await fixture.Admin.Client.SendAsync(deltaRequest);
        var delta = (await deltaResponse.Content.ReadFromJsonAsync<SessionEventSnapshot>())!;
        Assert.Single(delta.Items);
        Assert.Empty(delta.TranslationSequences);
        Assert.Equal(first.TranslationError, delta.TranslationError);
        using var recoveredRules = await fixture.Admin.SendAsync(HttpMethod.Put, $"/api/sessions/{session.Id}/configuration",
            new { translationDanmuPatternOverride = "^a+$", expectedRevision = 2 });
        Assert.Equal(HttpStatusCode.OK, recoveredRules.StatusCode);
        using var recoveryRequest = new HttpRequestMessage(HttpMethod.Get, PathFor(session) + "?cursor=" + Uri.EscapeDataString(delta.Cursor));
        recoveryRequest.Headers.AcceptLanguage.ParseAdd("en-US");
        using var recoveryResponse = await fixture.Admin.Client.SendAsync(recoveryRequest);
        var recovered = (await recoveryResponse.Content.ReadFromJsonAsync<SessionEventSnapshot>())!;
        Assert.True(recovered.Reset);
        Assert.Equal(4, recovered.Items.Count);
        Assert.Equal([1L, 4L], recovered.TranslationSequences);
        Assert.Null(recovered.TranslationError);
    }

    /// <summary>请求语言和 Gap 原文变化均使旧条件缓存与游标失效，按当前语言重建提示。</summary>
    [MongoHttpFact]
    public async Task LanguageAndGapChangesInvalidateEntityTagsAndCursors()
    {
        await using var fixture = await CreateFixtureAsync();
        var session = await SeedSessionAsync(fixture);
        await WriteAsync(fixture, session, new RecordedEventEnvelope
        {
            Kind = "Gap", OffsetMs = 1000, Text = "归档回退内容",
            TextCode = nameof(MessageCode.RECORDING_EVENT_CAPTURE_GAP), TextArguments = ["network1"]
        });
        using var chineseRequest = new HttpRequestMessage(HttpMethod.Get, PathFor(session));
        chineseRequest.Headers.AcceptLanguage.ParseAdd("zh-CN");
        using var chineseResponse = await fixture.Admin.Client.SendAsync(chineseRequest);
        var chinese = (await chineseResponse.Content.ReadFromJsonAsync<SessionEventSnapshot>())!;
        Assert.Equal("弹幕采集缺失：network1", Assert.Single(chinese.Items).Text);
        using var englishRequest = new HttpRequestMessage(HttpMethod.Get, PathFor(session) + "?cursor=" + Uri.EscapeDataString(chinese.Cursor));
        englishRequest.Headers.AcceptLanguage.ParseAdd("en-US");
        englishRequest.Headers.IfNoneMatch.Add(chineseResponse.Headers.ETag!);
        using var englishResponse = await fixture.Admin.Client.SendAsync(englishRequest);
        Assert.Equal(HttpStatusCode.OK, englishResponse.StatusCode);
        var english = (await englishResponse.Content.ReadFromJsonAsync<SessionEventSnapshot>())!;
        Assert.True(english.Reset);
        Assert.Equal("Event capture was interrupted: network1", Assert.Single(english.Items).Text);
        Assert.NotEqual(chineseResponse.Headers.ETag, englishResponse.Headers.ETag);
        Assert.NotEqual(chinese.Version, english.Version);
        using var unchangedRequest = new HttpRequestMessage(HttpMethod.Get, PathFor(session) + "?cursor=" + Uri.EscapeDataString(english.Cursor));
        unchangedRequest.Headers.AcceptLanguage.ParseAdd("en-US");
        unchangedRequest.Headers.IfNoneMatch.Add(englishResponse.Headers.ETag!);
        using var unchanged = await fixture.Admin.Client.SendAsync(unchangedRequest);
        Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);
        var archivePath = Path.Combine(fixture.StoragePath, session.DirectoryPath, "弹幕", "事件.jsonl");
        var contents = await File.ReadAllTextAsync(archivePath);
        await File.WriteAllTextAsync(archivePath, contents.Replace("network1", "network2", StringComparison.Ordinal));
        using var changedRequest = new HttpRequestMessage(HttpMethod.Get, PathFor(session) + "?cursor=" + Uri.EscapeDataString(english.Cursor));
        changedRequest.Headers.AcceptLanguage.ParseAdd("en-US");
        changedRequest.Headers.IfNoneMatch.Add(englishResponse.Headers.ETag!);
        using var changedResponse = await fixture.Admin.Client.SendAsync(changedRequest);
        var changed = (await changedResponse.Content.ReadFromJsonAsync<SessionEventSnapshot>())!;
        Assert.Equal(HttpStatusCode.OK, changedResponse.StatusCode);
        Assert.True(changed.Reset);
        Assert.Equal("Event capture was interrupted: network2", Assert.Single(changed.Items).Text);
        Assert.NotEqual(englishResponse.Headers.ETag, changedResponse.Headers.ETag);
    }

    /// <summary>只为快照 JSON 协商 Gzip，解压后保留完整消息和同传数据。</summary>
    [MongoHttpFact]
    public Task SnapshotNegotiatesGzipWithoutChangingOtherEndpoints() => VerifyCompressionAsync("gzip");

    /// <summary>只为快照 JSON 协商 Brotli，解压后保留完整消息和同传数据。</summary>
    [MongoHttpFact]
    public Task SnapshotNegotiatesBrotliWithoutChangingOtherEndpoints() => VerifyCompressionAsync("br");

    private static async Task VerifyCompressionAsync(string encoding)
    {
        await using var fixture = await CreateFixtureAsync();
        var session = await SeedSessionAsync(fixture);
        await WriteAsync(fixture, session, new RecordedEventEnvelope { OffsetMs = 1000, Text = "【" + new string('中', 6000) + "】" });
        using var request = new HttpRequestMessage(HttpMethod.Get, PathFor(session));
        request.Headers.AcceptEncoding.ParseAdd(encoding);
        using var response = await fixture.Admin.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(encoding, Assert.Single(response.Content.Headers.ContentEncoding));
        Assert.Contains("Accept-Encoding", response.Headers.Vary);
        Assert.True(response.Headers.ETag!.IsWeak);
        await using var compressed = await response.Content.ReadAsStreamAsync();
        await using Stream decoded = encoding == "br"
            ? new BrotliStream(compressed, CompressionMode.Decompress)
            : new GZipStream(compressed, CompressionMode.Decompress);
        var snapshot = (await JsonSerializer.DeserializeAsync<SessionEventSnapshot>(decoded, JsonFiles.Options))!;
        Assert.Equal(6002, Assert.Single(snapshot.Items).Text.Length);
        Assert.Equal([1L], snapshot.TranslationSequences);
        using var settingsRequest = new HttpRequestMessage(HttpMethod.Get, "/api/settings");
        settingsRequest.Headers.AcceptEncoding.ParseAdd(encoding);
        using var settings = await fixture.Admin.Client.SendAsync(settingsRequest);
        Assert.Equal(HttpStatusCode.OK, settings.StatusCode);
        Assert.Empty(settings.Content.Headers.ContentEncoding);
    }

    private static async Task<HttpServerFixture> CreateFixtureAsync()
    {
        var fixture = await HttpServerFixture.CreateAsync("Production", "--TranscodingControl:Enabled", "false");
        try
        {
            await fixture.InstallAsync();
            await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    private static async Task<BroadcastSession> SeedSessionAsync(HttpServerFixture fixture, bool live = false)
    {
        var start = DateTime.UtcNow.AddMinutes(-1);
        var session = new BroadcastSession
        {
            Title = "浏览器事件快照", DirectoryPath = "event-snapshot", HasDanmu = true,
            StartedAtUtc = start, EndedAtUtc = live ? null : start.AddSeconds(10), State = live ? "Recording" : "Completed"
        };
        await fixture.Context.Collection<BroadcastSession>("sessions").InsertOneAsync(session);
        await fixture.Context.Collection<RecordingSegment>("segments").InsertOneAsync(new()
        {
            SessionId = session.Id, RelativePath = "recording.flv", DurationMs = 10000, CommittedBytes = 64, IsClosed = !live
        });
        return session;
    }

    private static string PathFor(BroadcastSession session) => $"/api/sessions/{session.Id}/events/snapshot";

    private static async Task WriteAsync(HttpServerFixture fixture, BroadcastSession session, params RecordedEventEnvelope[] items)
    {
        await using var writer = new SessionEventWriter(Path.Combine(fixture.StoragePath, session.DirectoryPath, "弹幕"));
        foreach (var item in items)
        {
            await writer.WriteAsync(item);
        }
    }
}
