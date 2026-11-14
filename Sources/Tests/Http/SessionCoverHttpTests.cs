using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Xunit;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Files;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>验证缺失原图的封面接口能生成并复用真实录像帧，且维持成员鉴权。</summary>
public sealed class SessionCoverHttpTests
{
    [MongoHttpFact]
    public async Task MissingOriginalReturnsPersistedFrameFromOpenRecording()
    {
        await using var fixture = await PlaybackHttpMediaFixture.CreateAsync();
        await fixture.App.Services.GetServices<IHostedService>().OfType<ArtworkMaintenanceWorker>().Single().StopAsync(CancellationToken.None);
        var session = await PlaybackHttpMediaFixture.SeedRecordingAsync(fixture, 2);
        await fixture.Context.Collection<RecordingSegment>("segments").UpdateManyAsync(item => item.SessionId == session.Id,
            Builders<RecordingSegment>.Update.Set(item => item.IsClosed, false));
        var url = $"/api/sessions/{session.Id}/cover";
        var anonymous = fixture.NewBrowser();
        using var denied = await anonymous.Client.GetAsync(url);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        using var response = await fixture.Admin.Client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 100);
        Assert.Equal(new byte[] { 0xff, 0xd8 }, bytes[..2]);
        var stored = await fixture.Context.Collection<BroadcastSession>("sessions").Find(item => item.Id == session.Id).SingleAsync();
        Assert.NotNull(stored.CoverRelativePath);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(fixture.StoragePath, stored.CoverRelativePath)));
        using var second = await fixture.Admin.Client.GetAsync(url);
        Assert.Equal(bytes, await second.Content.ReadAsByteArrayAsync());
    }
}
