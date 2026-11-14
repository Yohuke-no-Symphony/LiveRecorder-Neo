using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Xunit;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Library;
using Yohuke.LiveRecorder.Infrastructure.Storage;

namespace Yohuke.LiveRecorder.Tests;

/// <summary>验证封面优先级、缺图恢复、持久化以及媒体生命周期竞态。</summary>
public sealed class SessionCoverTests
{
    [Fact]
    public async Task OriginalCoverIsSavedAndReusedWithoutExtractingFrames()
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var (session, _) = await fixture.SeedSessionAsync();
        await SetCoverUrlAsync(fixture, session, "https://i0.hdslb.com/cover.png");
        using var http = new AudienceArtworkHttpHandler();
        var frames = new SessionCoverTestFrameExtractor();
        using var service = CreateService(fixture, http, frames);

        var path = await service.GetAsync(session.Id);

        Assert.NotNull(path);
        Assert.EndsWith("直播封面.png", path);
        Assert.Equal("/cover.png", await File.ReadAllTextAsync(path));
        Assert.Equal(path, await service.GetAsync(session.Id));
        Assert.Single(http.Requests);
        Assert.Empty(frames.Requests);
        await AssertPersistedAsync(fixture, session, path);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://i0.hdslb.com/unavailable.png")]
    public async Task MissingOriginalFallsBackOnceAcrossConcurrentRequests(string source)
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var (session, _) = await fixture.SeedSessionAsync();
        await SetCoverUrlAsync(fixture, session, source);
        using var http = new AudienceArtworkHttpHandler { StatusCode = HttpStatusCode.ServiceUnavailable };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var frames = new SessionCoverTestFrameExtractor
        {
            Handle = async (request, token) =>
            {
                entered.SetResult();
                await release.Task.WaitAsync(token);
                return await SessionCoverTestFrameExtractor.SaveAsync(request, token);
            }
        };
        using var service = CreateService(fixture, http, frames);
        var first = service.GetAsync(session.Id);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = service.GetAsync(session.Id);
        release.SetResult();
        var paths = await Task.WhenAll(first, second);

        Assert.NotNull(paths[0]);
        Assert.Equal(paths[0], paths[1]);
        Assert.EndsWith("直播封面.jpg", paths[0]);
        Assert.Single(frames.Requests);
        Assert.Equal(source.Length == 0 ? 0 : 1, http.Requests.Count);
        await AssertPersistedAsync(fixture, session, paths[0]!);
    }

    [Fact]
    public async Task PendingVideoCanRecoverOnLaterAttempt()
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var (session, _) = await fixture.SeedSessionAsync();
        using var http = new AudienceArtworkHttpHandler();
        var frames = new SessionCoverTestFrameExtractor { Handle = (_, _) => Task.FromResult(false) };
        using var service = CreateService(fixture, http, frames);

        Assert.Null(await service.GetAsync(session.Id));
        Assert.Null((await ReadSessionAsync(fixture, session.Id)).CoverRelativePath);
        frames.Handle = SessionCoverTestFrameExtractor.SaveAsync;
        Assert.NotNull(await service.GetAsync(session.Id));
        Assert.Equal(2, frames.Requests.Count);
    }

    [Fact]
    public async Task OriginalDownloadCanRecoverAfterTransientFailureWithoutVideo()
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var (session, _) = await fixture.SeedSessionAsync();
        await fixture.Mongo.Collection<RecordingSegment>("segments").DeleteManyAsync(item => item.SessionId == session.Id);
        await SetCoverUrlAsync(fixture, session, "https://i0.hdslb.com/retry.png");
        using var http = new AudienceArtworkHttpHandler { StatusCode = HttpStatusCode.ServiceUnavailable };
        var frames = new SessionCoverTestFrameExtractor();
        using var service = CreateService(fixture, http, frames);

        Assert.Null(await service.GetAsync(session.Id));
        http.StatusCode = HttpStatusCode.OK;
        Assert.NotNull(await service.GetAsync(session.Id));
        Assert.Empty(frames.Requests);
        Assert.Equal(2, http.Requests.Count);
    }

    [Fact]
    public async Task EmptyOriginalBodyFallsBackToFrame()
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var (session, _) = await fixture.SeedSessionAsync();
        await SetCoverUrlAsync(fixture, session, "https://i0.hdslb.com/empty.png");
        using var http = new AudienceArtworkHttpHandler { ImageStreamFactory = () => new MemoryStream() };
        var frames = new SessionCoverTestFrameExtractor();
        using var service = CreateService(fixture, http, frames);

        var path = await service.GetAsync(session.Id);

        Assert.NotNull(path);
        Assert.EndsWith("直播封面.jpg", path);
        Assert.True(new FileInfo(path).Length > 0);
        Assert.Single(frames.Requests);
    }

    [Fact]
    public async Task InterruptedOriginalBodyFallsBackToFrame()
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var (session, _) = await fixture.SeedSessionAsync();
        await SetCoverUrlAsync(fixture, session, "https://i0.hdslb.com/interrupted.png");
        using var http = new AudienceArtworkHttpHandler { ImageStreamFactory = () => new CoverTestImageStream(false) };
        var frames = new SessionCoverTestFrameExtractor();
        using var service = CreateService(fixture, http, frames);

        Assert.NotNull(await service.GetAsync(session.Id));
        Assert.Single(frames.Requests);
    }

    [Fact]
    public async Task OriginalBodyDeadlineFallsBackToFrame()
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var (session, _) = await fixture.SeedSessionAsync();
        await SetCoverUrlAsync(fixture, session, "https://i0.hdslb.com/stalled.png");
        using var http = new AudienceArtworkHttpHandler { ImageStreamFactory = () => new CoverTestImageStream(true) };
        var frames = new SessionCoverTestFrameExtractor();
        using var service = CreateService(fixture, http, frames);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(25));

        Assert.NotNull(await service.GetAsync(session.Id, cancellation.Token));
        Assert.Single(frames.Requests);
    }

    [Fact]
    public async Task CorruptFirstSegmentDoesNotBlockGroupedSegment()
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var (session, segments) = await fixture.SeedSessionAsync(count: 2);
        await fixture.Mongo.Collection<RecordingSegment>("segments").UpdateOneAsync(item => item.Id == segments[1].Id,
            Builders<RecordingSegment>.Update.Set(item => item.RootRelativePath, "shared/video.flv")
                .Set(item => item.RootIndexRelativePath, "shared/video.index.jsonl"));
        using var http = new AudienceArtworkHttpHandler();
        var frames = new SessionCoverTestFrameExtractor
        {
            Handle = (request, token) => request.SourcePath.EndsWith("canonical_0.flv", StringComparison.Ordinal)
                ? throw new InvalidDataException("broken segment")
                : SessionCoverTestFrameExtractor.SaveAsync(request, token)
        };
        using var service = CreateService(fixture, http, frames);

        Assert.NotNull(await service.GetAsync(session.Id));
        Assert.Equal(2, frames.Requests.Count);
        var request = frames.Requests.Last();
        Assert.Equal(Path.Combine(fixture.Root, "shared", "video.flv"), request.SourcePath);
        Assert.Equal(Path.Combine(fixture.Root, "shared", "video.index.jsonl"), request.IndexPath);
    }

    [Fact]
    public async Task TrashingDuringExtractionDoesNotRecreateSessionDirectory()
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var (session, _) = await fixture.SeedSessionAsync();
        using var http = new AudienceArtworkHttpHandler();
        var trash = new SessionTrashService(fixture.Mongo, fixture.Settings, fixture.Paths);
        var frames = new SessionCoverTestFrameExtractor
        {
            Handle = async (request, token) =>
            {
                await trash.TrashAsync(session.Id, "owner", token);
                return await SessionCoverTestFrameExtractor.SaveAsync(request, token);
            }
        };
        using var service = CreateService(fixture, http, frames);

        Assert.Null(await service.GetAsync(session.Id));
        var stored = await ReadSessionAsync(fixture, session.Id);
        Assert.True(stored.IsTrashed);
        Assert.Null(stored.CoverRelativePath);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, session.DirectoryPath)));
        Assert.Empty(Directory.EnumerateFiles(fixture.Paths.CacheDirectory, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ChangedMediaRevisionDiscardsStaleFrame()
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var (session, _) = await fixture.SeedSessionAsync();
        using var http = new AudienceArtworkHttpHandler();
        var frames = new SessionCoverTestFrameExtractor
        {
            Handle = async (request, token) =>
            {
                await fixture.Mongo.Collection<BroadcastSession>("sessions").UpdateOneAsync(item => item.Id == session.Id,
                    Builders<BroadcastSession>.Update.Inc(item => item.MediaRevision, 1), cancellationToken: token);
                return await SessionCoverTestFrameExtractor.SaveAsync(request, token);
            }
        };
        using var service = CreateService(fixture, http, frames);

        Assert.Null(await service.GetAsync(session.Id));
        Assert.Null((await ReadSessionAsync(fixture, session.Id)).CoverRelativePath);
        Assert.False(File.Exists(Path.Combine(fixture.Root, session.DirectoryPath, "直播封面.jpg")));
    }

    [Fact]
    public async Task MaintenanceEventuallyReachesSessionsAfterTwentyFailures()
    {
        await using var fixture = await JobTestContext.CreateAsync();
        await fixture.Mongo.Collection<BroadcastSession>("sessions").InsertManyAsync(Enumerable.Range(0, 20).Select(index =>
            new BroadcastSession { Id = $"!pending-{index:D2}", DirectoryPath = $"pending/{index}" }));
        var (session, _) = await fixture.SeedSessionAsync();
        using var http = new AudienceArtworkHttpHandler();
        var generated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var frames = new SessionCoverTestFrameExtractor
        {
            Handle = async (request, token) =>
            {
                var saved = await SessionCoverTestFrameExtractor.SaveAsync(request, token);
                generated.TrySetResult();
                return saved;
            }
        };
        using var service = CreateService(fixture, http, frames);
        using var worker = new ArtworkMaintenanceWorker(fixture.Mongo, fixture.Bootstrap, new(http, fixture.Paths),
            service, NullLogger<ArtworkMaintenanceWorker>.Instance, fixture.Paths);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await generated.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Single(frames.Requests);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private static SessionCoverService CreateService(JobTestContext fixture, AudienceArtworkHttpHandler http, SessionCoverTestFrameExtractor frames) =>
        new(fixture.Mongo, new LibraryService(fixture.Mongo, TimeProvider.System), new ArtworkCache(http, fixture.Paths),
            frames, fixture.Paths, NullLogger<SessionCoverService>.Instance);

    private static Task<BroadcastSession> ReadSessionAsync(JobTestContext fixture, string id) =>
        fixture.Mongo.Collection<BroadcastSession>("sessions").Find(item => item.Id == id).SingleAsync();

    private static Task SetCoverUrlAsync(JobTestContext fixture, BroadcastSession session, string url) =>
        fixture.Mongo.Collection<BroadcastSession>("sessions").UpdateOneAsync(item => item.Id == session.Id,
            Builders<BroadcastSession>.Update.Set(item => item.CoverUrl, url));

    private static async Task AssertPersistedAsync(JobTestContext fixture, BroadcastSession session, string path)
    {
        var stored = await ReadSessionAsync(fixture, session.Id);
        Assert.Equal(Path.GetRelativePath(fixture.Root, path), stored.CoverRelativePath);
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(fixture.Root, session.DirectoryPath, "封面.json")));
        Assert.Equal(stored.CoverRelativePath, manifest.RootElement.GetProperty("coverRelativePath").GetString());
    }
}
