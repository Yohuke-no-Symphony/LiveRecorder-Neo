using System.Net;
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

internal sealed class BandwidthHttpFixture : IAsyncDisposable
{
    internal HttpServerFixture Server { get; private set; } = null!;
    internal HttpMediaFixtureData Media { get; private set; } = null!;
    internal byte[] Bytes { get; private set; } = [];
    internal string SegmentUrl { get; private set; } = "";
    internal string ClipUrl => $"/api/clips/{Media.Clip.Id}/download";
    internal string OutputUrl => $"/api/jobs/{Media.Job.Id}/outputs/0";
    internal string ClipMediaUrl => $"/api/clips/{Media.Clip.Id}/media";
    internal string PlaybackMediaUrl => Media.PlaybackUrl.Replace("index.m3u8", "segment0.ts", StringComparison.Ordinal);

    internal static async Task<BandwidthHttpFixture> CreateAsync(int bytes)
    {
        var fixture = new BandwidthHttpFixture();
        try
        {
            fixture.Server = await HttpServerFixture.CreateAsync();
            await fixture.Server.InstallAsync();
            await fixture.Server.App.Services.GetServices<IHostedService>().OfType<RecordingSupervisor>().Single().StopAsync(CancellationToken.None);
            await fixture.Server.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
            var administrator = await fixture.Server.Context.Collection<UserAccount>("users").Find(user => user.Role == "Admin").SingleAsync();
            fixture.Media = await HttpMediaFixtureData.CreateAsync(fixture.Server, administrator.Id);
            fixture.Bytes = Enumerable.Range(0, bytes).Select(value => (byte)(value % 251)).ToArray();
            var segment = await fixture.Server.Context.Collection<RecordingSegment>("segments").Find(item => item.SessionId == fixture.Media.Session.Id).SingleAsync();
            fixture.SegmentUrl = $"/api/sessions/{fixture.Media.Session.Id}/segments/{segment.Id}/download";
            var recordingPath = MediaPaths.ResolveSegment(fixture.Server.StoragePath, fixture.Media.Session.DirectoryPath, segment);
            var clipPath = MediaPaths.Resolve(fixture.Server.StoragePath, fixture.Media.Clip.RelativePath);
            var playbackPath = Path.Combine(fixture.Server.CachePath, "playback", fixture.PlaybackMediaUrl["/api/playback/".Length..]);
            foreach (var path in new[] { recordingPath, clipPath, playbackPath })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path, fixture.Bytes);
                File.SetLastWriteTimeUtc(path, new(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc));
            }
            await fixture.Server.Context.Collection<RecordingSegment>("segments").UpdateOneAsync(item => item.Id == segment.Id,
                Builders<RecordingSegment>.Update.Set(item => item.CommittedBytes, bytes));
            await fixture.Server.Context.Collection<Clip>("clips").UpdateOneAsync(item => item.Id == fixture.Media.Clip.Id,
                Builders<Clip>.Update.Set(item => item.Bytes, bytes));
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    internal async Task ConfigureAsync(long userLimit = 0, bool smart = false, long total = 0, int guarantee = 10)
    {
        var settings = await Server.Admin.Client.GetFromJsonAsync<JsonObject>("/api/settings");
        settings!["bandwidth"] = new JsonObject
        {
            ["userDownloadLimitBytesPerSecond"] = userLimit,
            ["smartLimitEnabled"] = smart,
            ["totalBytesPerSecond"] = total,
            ["downloadGuaranteedPercent"] = guarantee
        };
        using var response = await Server.Admin.SendAsync(HttpMethod.Put, "/api/settings", settings);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Server is not null)
        {
            await Server.DisposeAsync();
        }
    }
}
