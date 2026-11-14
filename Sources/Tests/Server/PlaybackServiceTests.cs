using MongoDB.Driver;
using Microsoft.Extensions.Options;
using Xunit;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Application.Media;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Library;
using Yohuke.LiveRecorder.Infrastructure.Playback;

namespace Yohuke.LiveRecorder.Tests;

/// <summary>验证播放分桶、缓存覆盖及取消发布边界。</summary>
public sealed class PlaybackServiceTests
{
    /// <summary>任意合法跳转均由对应的三十秒窗口覆盖。</summary>
    [Theory]
    [InlineData(10000, 0)]
    [InlineData(29999, 0)]
    [InlineData(30000, 30000)]
    [InlineData(80000, 60000)]
    [InlineData(119999, 90000)]
    public async Task ColdSeekBuildsShortWindowContainingTarget(double offsetMs, double expectedStart)
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var (session, segment) = await SeedAsync(fixture);
        var builder = new PlaybackServiceTestBuilder();
        var service = CreateService(fixture, builder);

        var response = await service.OpenAsync(session.Id, offsetMs);

        var request = Assert.Single(builder.Requests);
        Assert.Equal(expectedStart, request.SourceStartMs);
        Assert.Equal(30000, request.DurationMs);
        Assert.InRange(offsetMs, response.StartOffsetMs, Math.BitDecrement(response.EndOffsetMs));
        Assert.Equal(segment.DurationMs, response.DurationMs);
        Assert.True(File.Exists(await ResolvePlaylistAsync(service, response)));
    }

    /// <summary>仍在缓存覆盖内的尾部跳转不重复生成媒体。</summary>
    [Fact]
    public async Task CachedWindowIncludesPositionsImmediatelyBeforeItsEnd()
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var (session, _) = await SeedAsync(fixture);
        var builder = new PlaybackServiceTestBuilder();
        var service = CreateService(fixture, builder);
        var first = await service.OpenAsync(session.Id, 1000);

        var cached = await service.OpenAsync(session.Id, first.EndOffsetMs - 10);

        Assert.Equal(first.Url, cached.Url);
        Assert.Single(builder.Requests);
    }

    /// <summary>保留正常音视频起点偏差的真实映射，零点定位仍可播放和复用。</summary>
    [Fact]
    public async Task ZeroSeekToleratesSmallVideoStartOffsetWithoutChangingSourceTime()
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var (session, _) = await SeedAsync(fixture);
        var builder = new PlaybackServiceTestBuilder
        {
            Build = async (request, token) =>
            {
                var result = await PlaybackServiceTestBuilder.PublishAsync(request, token);
                result.SourceStartMs = 21;
                return result;
            }
        };
        var service = CreateService(fixture, builder);

        var initial = await service.OpenAsync(session.Id, 0);
        var cached = await service.OpenAsync(session.Id, 0);

        Assert.Equal(21, initial.StartOffsetMs);
        Assert.Equal(0, initial.RequestedOffsetMs);
        Assert.Equal(initial.Url, cached.Url);
        Assert.Single(builder.Requests);
    }

    /// <summary>构建器返回不覆盖目标的实际关键帧范围时拒绝发布成功缓存。</summary>
    [Theory]
    [InlineData(5000, 30000, 1000)]
    [InlineData(101, 30000, 0)]
    [InlineData(80, 50, 30)]
    public async Task WindowThatDoesNotCoverTargetIsNotPublished(double startMs, double endMs, double targetMs)
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var (session, _) = await SeedAsync(fixture);
        var builder = new PlaybackServiceTestBuilder
        {
            Build = async (request, token) =>
            {
                var result = await PlaybackServiceTestBuilder.PublishAsync(request, token);
                result.SourceStartMs = startMs;
                result.SourceEndMs = endMs;
                return result;
            }
        };

        var error = await Assert.ThrowsAsync<InfrastructureException>(() => CreateService(fixture, builder).OpenAsync(session.Id, targetMs));

        Assert.Equal(425, error.StatusCode);
        Assert.Equal(nameof(MessageCode.PLAYBACK_WINDOW_NOT_READY), error.Code);
        Assert.False(Directory.Exists(Assert.Single(builder.Requests).OutputDirectory));
        Assert.Empty(Directory.EnumerateFiles(fixture.Paths.CacheDirectory, "latest.json", SearchOption.AllDirectories));
    }

    /// <summary>新窗口已经写出媒体但取消元数据提交时保留原代次并清理新目录。</summary>
    [Fact]
    public async Task CancellationAfterMediaPublicationPreservesPreviousGeneration()
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var (session, segment) = await SeedAsync(fixture, 10000);
        var builder = new PlaybackServiceTestBuilder();
        var service = CreateService(fixture, builder);
        var original = await service.OpenAsync(session.Id, 1000);
        var originalPlaylist = await ResolvePlaylistAsync(service, original);
        await fixture.Mongo.Collection<RecordingSegment>("segments").UpdateOneAsync(x => x.Id == segment.Id,
            Builders<RecordingSegment>.Update.Set(x => x.DurationMs, 120000));
        using var cancelled = new CancellationTokenSource();
        builder.Build = async (request, token) =>
        {
            var result = await PlaybackServiceTestBuilder.PublishAsync(request, token);
            await cancelled.CancelAsync();
            return result;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.OpenAsync(session.Id, 15000, cancelled.Token));

        Assert.True(File.Exists(originalPlaylist));
        Assert.False(Directory.Exists(builder.Requests[1].OutputDirectory));
        builder.Build = null;
        var cached = await service.OpenAsync(session.Id, 1000);
        Assert.Equal(original.Url, cached.Url);
        Assert.Equal(2, builder.Requests.Count);
    }

    /// <summary>变更窗口配置后使用新缓存键，分桶与请求时长保持一致。</summary>
    [Fact]
    public async Task WindowDurationChangesCacheIdentityAndSelectionTogether()
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var (session, _) = await SeedAsync(fixture);
        var builder = new PlaybackServiceTestBuilder();
        var standard = await CreateService(fixture, builder).OpenAsync(session.Id, 20000);

        var shorter = await CreateService(fixture, builder, 15000).OpenAsync(session.Id, 20000);

        Assert.NotEqual(standard.Url, shorter.Url);
        Assert.Equal(15000, builder.Requests[1].SourceStartMs);
        Assert.Equal(15000, builder.Requests[1].DurationMs);
        Assert.Equal(15000, shorter.StartOffsetMs);
        Assert.Equal(30000, shorter.EndOffsetMs);
    }

    /// <summary>空闲预取成功后普通播放直接复用同一完整窗口。</summary>
    [Fact]
    public async Task ForegroundRequestReusesCompletedPrefetch()
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var (session, _) = await SeedAsync(fixture);
        var builder = new PlaybackServiceTestBuilder();
        var service = CreateService(fixture, builder);

        var prefetched = await service.TryPrefetchAsync(session.Id, 30000);
        var response = await service.OpenAsync(session.Id, 40000);

        Assert.NotNull(prefetched);
        Assert.Equal(prefetched.Url, response.Url);
        Assert.Single(builder.Requests);
    }

    /// <summary>预取没有进程额度时不创建缓存代次且普通播放仍可重试。</summary>
    [Fact]
    public async Task SkippedPrefetchDoesNotPublishAnEmptyCache()
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var (session, _) = await SeedAsync(fixture);
        var builder = new PlaybackServiceTestBuilder { SkipPrefetch = true };
        var service = CreateService(fixture, builder);

        Assert.Null(await service.TryPrefetchAsync(session.Id, 40000));
        Assert.Empty(Directory.EnumerateFiles(fixture.Paths.CacheDirectory, "*", SearchOption.AllDirectories));
        Assert.NotNull(await service.OpenAsync(session.Id, 40000));
        Assert.Single(builder.Requests);
    }

    /// <summary>同窗生成中预取立即跳过，前台等待者在构建完成后复用结果。</summary>
    [Fact]
    public async Task BusyWindowSkipsPrefetchAndSharesCompletedCacheWithForegroundWaiter()
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var (session, _) = await SeedAsync(fixture);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = new PlaybackServiceTestBuilder
        {
            Build = async (request, token) =>
            {
                started.TrySetResult();
                await release.Task.WaitAsync(token);
                return await PlaybackServiceTestBuilder.PublishAsync(request, token);
            }
        };
        var service = CreateService(fixture, builder);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var first = service.OpenAsync(session.Id, 40000, timeout.Token);
        await started.Task.WaitAsync(timeout.Token);
        try
        {
            Assert.Null(await service.TryPrefetchAsync(session.Id, 45000, timeout.Token).WaitAsync(TimeSpan.FromSeconds(2)));
            var next = service.OpenAsync(session.Id, 45000, timeout.Token);
            release.TrySetResult();
            Assert.Equal((await first).Url, (await next).Url);
            Assert.Single(builder.Requests);
        }
        finally
        {
            release.TrySetResult();
            await first;
        }
    }

    /// <summary>容量清理不能移除仍在生成中的暂存目录。</summary>
    [Fact]
    public async Task CacheMaintenancePreservesWindowBeingBuilt()
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var (session, _) = await SeedAsync(fixture);
        fixture.Settings.Settings.CacheLimitBytes = 0;
        var staged = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = new PlaybackServiceTestBuilder
        {
            Build = async (request, token) =>
            {
                var temporary = request.OutputDirectory + ".building-test";
                Directory.CreateDirectory(temporary);
                await File.WriteAllTextAsync(Path.Combine(temporary, "chunk_00000.m4s"), "building", token);
                staged.TrySetResult(temporary);
                await release.Task.WaitAsync(token);
                Directory.Move(temporary, request.OutputDirectory);
                return await PlaybackServiceTestBuilder.PublishAsync(request, token);
            }
        };
        var service = CreateService(fixture, builder);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = service.OpenAsync(session.Id, 1000, timeout.Token);
        var directory = await staged.Task.WaitAsync(timeout.Token);
        try
        {
            await service.MaintainAsync(timeout.Token);
            Assert.True(Directory.Exists(directory));
        }
        finally
        {
            release.TrySetResult();
            await pending;
        }
    }

    private static PlaybackService CreateService(JobTestContext fixture, PlaybackServiceTestBuilder builder, double durationMs = 30000)
    {
        return new(new LibraryService(fixture.Mongo, TimeProvider.System), fixture.Settings, builder, fixture.Paths,
            Options.Create(new PlaybackWindowOptions { Duration = TimeSpan.FromMilliseconds(durationMs) }));
    }

    private static async Task<(BroadcastSession Session, RecordingSegment Segment)> SeedAsync(JobTestContext fixture, double durationMs = 120000)
    {
        var (session, segments) = await fixture.SeedSessionAsync();
        var segment = Assert.Single(segments);
        segment.DurationMs = durationMs;
        segment.IndexRelativePath = "source/canonical_0.index.jsonl";
        await fixture.Mongo.Collection<RecordingSegment>("segments").ReplaceOneAsync(x => x.Id == segment.Id, segment);
        return (session, segment);
    }

    private static Task<string> ResolvePlaylistAsync(PlaybackService service, PlaybackResponse response)
    {
        var pieces = response.Url!.Split('/');
        return service.ResolveAssetAsync(pieces[^3], pieces[^2], pieces[^1]);
    }
}
