using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Recording;
using Xunit;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>验证共享录播分段附件的身份边界、当前媒体版本和稳定源文件交付。</summary>
public sealed class SegmentDownloadHttpTests
{
    /// <summary>成员可下载归组后的原始文件并使用 Range，匿名用户不能访问。</summary>
    [MongoHttpFact]
    public async Task MemberDownloadsProjectedSourceAsTheOriginalRangedAttachment()
    {
        await using var fixture = await CreateFixtureAsync();
        var (session, segment, bytes, path) = await SeedAsync(fixture, true);
        var user = await fixture.CreateUserAsync("download-member@example.test");
        var member = fixture.NewBrowser();
        await member.LoginAsync(user.Email, HttpServerFixture.USER_PASSWORD);
        var url = Url(session.Id, segment.Id);
        var anonymous = fixture.NewBrowser();
        using (var denied = await anonymous.Client.GetAsync(url))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
        using (var full = await member.Client.GetAsync(url))
        {
            Assert.Equal(HttpStatusCode.OK, full.StatusCode);
            Assert.Equal("video/x-flv", full.Content.Headers.ContentType!.MediaType);
            Assert.Equal("attachment", full.Content.Headers.ContentDisposition!.DispositionType);
            Assert.Equal(Path.GetFileName(path), full.Content.Headers.ContentDisposition.FileNameStar);
            Assert.Equal(bytes, await full.Content.ReadAsByteArrayAsync());
        }
        using (var request = new HttpRequestMessage(HttpMethod.Get, url))
        {
            request.Headers.Range = new RangeHeaderValue(4, 12);
            using var response = await member.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
            Assert.Equal("bytes 4-12/64", response.Content.Headers.ContentRange!.ToString());
            Assert.Equal(bytes[4..13], await response.Content.ReadAsByteArrayAsync());
        }
        using (var request = new HttpRequestMessage(HttpMethod.Get, url))
        {
            request.Headers.Range = new RangeHeaderValue(100, 200);
            using var response = await member.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, response.StatusCode);
            Assert.Equal("bytes */64", response.Content.Headers.ContentRange!.ToString());
        }
    }

    /// <summary>拒绝其他场次、旧版本、未完成、无效或已回收的录制文件，并保留安全路径校验。</summary>
    [MongoHttpFact]
    public async Task OnlyCurrentClosedAndAvailableSegmentsCanBeDownloaded()
    {
        await using var fixture = await CreateFixtureAsync();
        var (session, segment, _, path) = await SeedAsync(fixture, false);
        var url = Url(session.Id, segment.Id);
        var other = new RecordingSegment
        {
            SessionId = Guid.NewGuid().ToString("N"), RelativePath = segment.RelativePath,
            DurationMs = 1000, CommittedBytes = 64, IsClosed = true
        };
        var stale = new RecordingSegment
        {
            SessionId = session.Id, RelativePath = segment.RelativePath, Revision = 0,
            DurationMs = 1000, CommittedBytes = 64, IsClosed = true
        };
        await fixture.Context.Collection<RecordingSegment>("segments").InsertManyAsync([other, stale]);
        await AssertProblemAsync(fixture, Url(session.Id, other.Id), HttpStatusCode.NotFound, "LIBRARY_SEGMENT_NOT_FOUND");
        await AssertProblemAsync(fixture, Url(session.Id, stale.Id), HttpStatusCode.NotFound, "LIBRARY_SEGMENT_NOT_FOUND");
        await fixture.Context.Collection<RecordingSegment>("segments").UpdateOneAsync(item => item.Id == segment.Id,
            Builders<RecordingSegment>.Update.Set(item => item.IsClosed, false));
        await AssertProblemAsync(fixture, url, HttpStatusCode.Conflict, "LIBRARY_SEGMENT_NOT_READY");
        await fixture.Context.Collection<RecordingSegment>("segments").UpdateOneAsync(item => item.Id == segment.Id,
            Builders<RecordingSegment>.Update.Set(item => item.IsClosed, true).Set(item => item.DurationMs, 0));
        await AssertProblemAsync(fixture, url, HttpStatusCode.Conflict, "LIBRARY_SEGMENT_NOT_READY");
        await fixture.Context.Collection<RecordingSegment>("segments").UpdateOneAsync(item => item.Id == segment.Id,
            Builders<RecordingSegment>.Update.Set(item => item.DurationMs, 1000).Set(item => item.CommittedBytes, 12));
        await AssertProblemAsync(fixture, url, HttpStatusCode.Conflict, "LIBRARY_SEGMENT_NOT_READY");
        await fixture.Context.Collection<RecordingSegment>("segments").UpdateOneAsync(item => item.Id == segment.Id,
            Builders<RecordingSegment>.Update.Set(item => item.CommittedBytes, 64).Set(item => item.RootRelativePath, "../settings.json"));
        await AssertProblemAsync(fixture, url, HttpStatusCode.BadRequest, "PATH_MEDIA_OUTSIDE_STORAGE");
        await fixture.Context.Collection<RecordingSegment>("segments").UpdateOneAsync(item => item.Id == segment.Id,
            Builders<RecordingSegment>.Update.Set(item => item.RootRelativePath, null));
        File.Delete(path);
        await AssertProblemAsync(fixture, url, HttpStatusCode.Gone, "LIBRARY_FILE_NOT_FOUND");
        await fixture.Context.Collection<BroadcastSession>("sessions").UpdateOneAsync(item => item.Id == session.Id,
            Builders<BroadcastSession>.Update.Set(item => item.IsTrashed, true));
        await AssertProblemAsync(fixture, url, HttpStatusCode.NotFound, "LIBRARY_SESSION_NOT_FOUND");
    }

    private static async Task<HttpServerFixture> CreateFixtureAsync()
    {
        var fixture = await HttpServerFixture.CreateAsync();
        try
        {
            await fixture.InstallAsync();
            var supervisor = fixture.App.Services.GetServices<IHostedService>().OfType<RecordingSupervisor>().Single();
            await supervisor.StopAsync(CancellationToken.None);
            await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    private static async Task<(BroadcastSession Session, RecordingSegment Segment, byte[] Bytes, string Path)> SeedAsync(HttpServerFixture fixture, bool projected)
    {
        var start = DateTime.UtcNow.AddMinutes(-1);
        var session = new BroadcastSession
        {
            RoomId = 100, Title = "分段下载回归", DirectoryPath = Path.Combine("download-http", Guid.NewGuid().ToString("N")),
            StartedAtUtc = start, EndedAtUtc = start.AddSeconds(1), LastMediaAtUtc = start.AddSeconds(1), State = "Completed"
        };
        var segment = new RecordingSegment
        {
            SessionId = session.Id, RelativePath = Path.Combine("录像", "原始录像_001.flv"), DurationMs = 1000,
            CommittedBytes = 64, IsClosed = true,
            RootRelativePath = projected ? Path.Combine("source-http", session.Id, "原始录像_001.flv") : null
        };
        var path = MediaPaths.ResolveSegment(fixture.StoragePath, session.DirectoryPath, segment);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = Enumerable.Range(0, 64).Select(value => (byte)value).ToArray();
        await File.WriteAllBytesAsync(path, bytes);
        await fixture.Context.Collection<BroadcastSession>("sessions").InsertOneAsync(session);
        await fixture.Context.Collection<RecordingSegment>("segments").InsertOneAsync(segment);
        return (session, segment, bytes, path);
    }

    private static string Url(string sessionId, string segmentId) => $"/api/sessions/{sessionId}/segments/{segmentId}/download";

    private static async Task AssertProblemAsync(HttpServerFixture fixture, string url, HttpStatusCode status, string code)
    {
        using var response = await fixture.Admin.Client.GetAsync(url);
        Assert.Equal(status, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(code, error!["code"]!.GetValue<string>());
    }
}
