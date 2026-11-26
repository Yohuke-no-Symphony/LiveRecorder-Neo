using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>验证原录播已丢失的切片仍能生成、共享和复用实际视频封面。</summary>
public sealed class ClipCoverHttpTests
{
    [MongoHttpFact]
    public async Task ExistingMp4WithoutSourceRecordingProvidesTheSameCoverToOwnerAndShareViewer()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        var owner = await fixture.CreateUserAsync("clip-cover-owner@example.test");
        var viewer = await fixture.CreateUserAsync("clip-cover-viewer@example.test");
        var ownerBrowser = fixture.NewBrowser();
        var viewerBrowser = fixture.NewBrowser();
        await ownerBrowser.LoginAsync(owner.Email, HttpServerFixture.USER_PASSWORD);
        await viewerBrowser.LoginAsync(viewer.Email, HttpServerFixture.USER_PASSWORD);

        var clip = new Clip
        {
            Title = "旧切片",
            OwnerId = owner.Id,
            SessionId = "missing-source-recording",
            RelativePath = Path.Combine("历史切片", "片段.mp4"),
            EndMs = 2000,
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
            ShareToken = "legacy-cover-token"
        };
        var path = Path.Combine(fixture.StoragePath, clip.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var settings = await fixture.App.Services.GetRequiredService<ISettingsStore>().GetAsync();
        await PlaybackHttpMediaFixture.RunFfmpegAsync(settings.FfmpegPath,
        [
            "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=480x270:rate=12",
            "-t", "2", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", path
        ]);
        clip.Bytes = new FileInfo(path).Length;
        await fixture.Context.Collection<Clip>("clips").InsertOneAsync(clip);

        var personal = $"/api/clips/{clip.Id}/cover";
        var shared = $"/api/clips/{clip.Id}/share/{clip.ShareToken}/cover";
        using var sharedCover = await viewerBrowser.Client.GetAsync(shared);
        Assert.Equal(HttpStatusCode.OK, sharedCover.StatusCode);
        Assert.Equal("image/jpeg", sharedCover.Content.Headers.ContentType?.MediaType);
        Assert.True(sharedCover.Headers.CacheControl?.NoStore);
        var bytes = await sharedCover.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 100);
        Assert.Equal(new byte[] { 0xff, 0xd8 }, bytes[..2]);
        var cached = Assert.Single(Directory.EnumerateFiles(Path.Combine(fixture.CachePath, "artwork"), "*.image"));
        var lastWrite = File.GetLastWriteTimeUtc(cached);

        using var denied = await viewerBrowser.Client.GetAsync(personal);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var ownCover = await ownerBrowser.Client.GetAsync(personal);
        Assert.Equal(HttpStatusCode.OK, ownCover.StatusCode);
        Assert.Equal(bytes, await ownCover.Content.ReadAsByteArrayAsync());
        Assert.Equal(lastWrite, File.GetLastWriteTimeUtc(cached));
        Assert.Equal(cached, Assert.Single(Directory.EnumerateFiles(Path.GetDirectoryName(cached)!, "*.image")));

        var original = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl6hAAAAABJRU5ErkJggg==");
        var originalPath = Path.Combine("历史切片", "封面.png");
        await File.WriteAllBytesAsync(Path.Combine(fixture.StoragePath, originalPath), original);
        await fixture.Context.Collection<BroadcastSession>("sessions").InsertOneAsync(new()
        {
            Id = clip.SessionId,
            State = "Completed",
            DirectoryPath = "历史切片",
            CoverRelativePath = originalPath
        });
        Assert.Equal(original, await ownerBrowser.Client.GetByteArrayAsync(personal));
        Assert.Equal(original, await viewerBrowser.Client.GetByteArrayAsync(shared));
    }
}
