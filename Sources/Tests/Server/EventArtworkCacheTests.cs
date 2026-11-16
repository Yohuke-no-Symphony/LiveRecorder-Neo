using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Xunit;

namespace Yohuke.LiveRecorder.Tests;

/// <summary>验证旧图片地址规范化和 B 站 CDN 请求上下文。</summary>
public sealed class EventArtworkCacheTests
{
    [Fact]
    public async Task HistoricalHttpImageUsesHttpsAndBilibiliHeaders()
    {
        var directory = Path.Combine(Path.GetTempPath(), "recorder-artwork-" + Guid.NewGuid().ToString("N"));
        using var http = new AudienceArtworkHttpHandler();
        using var cache = new ArtworkCache(http, new AppPaths(directory, Path.Combine(directory, "records")));
        try
        {
            using var image = await cache.GetAsync("http://i0.hdslb.com/emote.png");

            Assert.NotNull(image);
            var request = Assert.Single(http.Requests);
            Assert.Equal("https", request.Source.Scheme);
            Assert.Equal("https://live.bilibili.com/", request.Referer);
            Assert.NotEmpty(request.UserAgent);
            Assert.Equal("image/png", image.ContentType);
            Assert.Equal("image/png", await File.ReadAllTextAsync(image.Path + ".type"));
            Assert.Equal("/emote.png", await File.ReadAllTextAsync(image.Path));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Theory]
    [InlineData("https://outside.example/avatar.png")]
    [InlineData("https://i0.hdslb.com.evil.example/avatar.png")]
    [InlineData("file:///etc/passwd")]
    public async Task DisallowedImageNeverSendsRequest(string source)
    {
        using var http = new AudienceArtworkHttpHandler();
        var directory = Path.Combine(Path.GetTempPath(), "recorder-unused-artwork-" + Guid.NewGuid().ToString("N"));
        using var cache = new ArtworkCache(http, new AppPaths(directory, Path.Combine(directory, "records")));

        Assert.Null(await cache.GetAsync(source));
        Assert.Empty(http.Requests);
        Assert.False(Directory.Exists(directory));
    }

    /// <summary>同一图片的多个使用者分别释放租约，重复释放不会使仍在使用的图片失去保护。</summary>
    [Fact]
    public async Task CleanupPreservesEachReaderUntilItsOwnLeaseIsReleased()
    {
        var directory = CreateDirectoryPath();
        using var http = new AudienceArtworkHttpHandler();
        using var cache = CreateCache(http, directory);
        try
        {
            using var first = await cache.GetAsync("https://i0.hdslb.com/shared.png");
            using var second = await cache.GetAsync("https://i0.hdslb.com/shared.png");
            Assert.NotNull(first);
            Assert.NotNull(second);
            var bytes = new FileInfo(first.Path).Length + new FileInfo(first.Path + ".type").Length;

            var initial = await cache.CleanupAsync(0);

            Assert.Equal(0, initial.DeletedFiles);
            Assert.True(initial.SkippedEntries > 0);
            first.Dispose();
            first.Dispose();
            var remainingReader = await cache.CleanupAsync(0);
            Assert.Equal(0, remainingReader.DeletedFiles);
            Assert.Equal("/shared.png", await File.ReadAllTextAsync(second.Path));
            second.Dispose();

            var released = await cache.CleanupAsync(0);

            Assert.Equal(2, released.DeletedFiles);
            Assert.Equal(bytes, released.DeletedBytes);
            Assert.False(File.Exists(second.Path));
            Assert.False(File.Exists(second.Path + ".type"));
            Assert.Equal(0, (await cache.CleanupAsync(0)).DeletedFiles);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    /// <summary>缓存命中刷新使用时间，保留图片本身的修改时间供 HTTP 条件请求使用。</summary>
    [Fact]
    public async Task CacheHitRefreshesUsageWithoutChangingImageModificationTime()
    {
        var directory = CreateDirectoryPath();
        using var http = new AudienceArtworkHttpHandler();
        using var cache = CreateCache(http, directory);
        try
        {
            using var original = await cache.GetAsync("https://i0.hdslb.com/refresh.png");
            Assert.NotNull(original);
            original.Dispose();
            var imageTime = DateTime.UtcNow.AddDays(-2);
            File.SetLastWriteTimeUtc(original.Path, imageTime);
            File.SetLastWriteTimeUtc(original.Path + ".type", imageTime);
            var savedImageTime = File.GetLastWriteTimeUtc(original.Path);

            using var cached = await cache.GetAsync("https://i0.hdslb.com/refresh.png");

            Assert.NotNull(cached);
            Assert.Equal(savedImageTime, File.GetLastWriteTimeUtc(cached.Path));
            Assert.True(File.GetLastWriteTimeUtc(cached.Path + ".type") > DateTime.UtcNow.AddMinutes(-1));
            cached.Dispose();
            Assert.Equal(0, (await cache.CleanupAsync(30)).DeletedFiles);
            Assert.Single(http.Requests);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    /// <summary>最短未使用时间依据图片类型文件的访问标记，图片内容修改时间不影响选择。</summary>
    [Fact]
    public async Task MinimumUnusedTimeSelectsOnlyImagesWithOldUsageMarkers()
    {
        var directory = CreateDirectoryPath();
        using var http = new AudienceArtworkHttpHandler();
        using var cache = CreateCache(http, directory);
        try
        {
            using var old = await cache.GetAsync("https://i0.hdslb.com/old.png");
            using var recent = await cache.GetAsync("https://i0.hdslb.com/recent.png");
            Assert.NotNull(old);
            Assert.NotNull(recent);
            old.Dispose();
            recent.Dispose();
            File.SetLastWriteTimeUtc(old.Path + ".type", DateTime.UtcNow.AddHours(-2));
            File.SetLastWriteTimeUtc(recent.Path, DateTime.UtcNow.AddDays(-2));

            var result = await cache.CleanupAsync(30);

            Assert.Equal(2, result.DeletedFiles);
            Assert.False(File.Exists(old.Path));
            Assert.True(File.Exists(recent.Path));
            Assert.True(File.Exists(recent.Path + ".type"));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    /// <summary>只删除当前图片缓存格式及其固定暂存文件，未知名称和目录中的内容保持不变。</summary>
    [Fact]
    public async Task CleanupDeletesOnlyWhitelistedImageAndTemporaryFiles()
    {
        var directory = CreateDirectoryPath();
        using var http = new AudienceArtworkHttpHandler();
        using var cache = CreateCache(http, directory);
        var artwork = Path.Combine(directory, "cache", "artwork");
        try
        {
            Directory.CreateDirectory(artwork);
            var stem = new string('a', 64) + ".image";
            foreach (var suffix in new[] { "", ".type", ".tmp", ".type.tmp" })
            {
                await File.WriteAllTextAsync(Path.Combine(artwork, stem + suffix), "cache");
            }
            var unknown = new[] { "unknown.image", new string('g', 64) + ".image", new string('a', 64) + ".json", stem + ".other.tmp" };
            foreach (var name in unknown)
            {
                await File.WriteAllTextAsync(Path.Combine(artwork, name), "keep");
            }
            var nested = Path.Combine(artwork, "unknown", stem);
            Directory.CreateDirectory(Path.GetDirectoryName(nested)!);
            await File.WriteAllTextAsync(nested, "keep");

            var result = await cache.CleanupAsync(0);

            Assert.Equal(4, result.DeletedFiles);
            Assert.Equal(20, result.DeletedBytes);
            Assert.All(unknown, name => Assert.True(File.Exists(Path.Combine(artwork, name))));
            Assert.True(File.Exists(nested));
            Assert.True(result.SkippedEntries >= unknown.Length);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    /// <summary>图片下载占用分片时清理跳过该键的暂存文件，而非等待或删除未完成内容。</summary>
    [Fact]
    public async Task CleanupSkipsImageWhileDownloadOwnsItsShard()
    {
        var directory = CreateDirectoryPath();
        using var http = new AudienceArtworkHttpHandler();
        using var cache = CreateCache(http, directory);
        var pipe = new Pipe();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        http.ImageStreamFactory = () =>
        {
            started.TrySetResult();
            return pipe.Reader.AsStream();
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task<ArtworkCacheLease?>? pending = null;
        try
        {
            const string SOURCE = "https://i0.hdslb.com/downloading.png";
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(SOURCE))).ToLowerInvariant();
            var temporary = Path.Combine(directory, "cache", "artwork", key + ".image.tmp");
            Directory.CreateDirectory(Path.GetDirectoryName(temporary)!);
            await File.WriteAllTextAsync(temporary, "staged", timeout.Token);
            pending = cache.GetAsync(SOURCE, timeout.Token);
            await started.Task.WaitAsync(timeout.Token);

            var result = await cache.CleanupAsync(0, timeout.Token);

            Assert.Equal(0, result.DeletedFiles);
            Assert.True(result.SkippedEntries > 0);
            Assert.True(File.Exists(temporary));
            await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes("downloaded"), timeout.Token);
            await pipe.Writer.CompleteAsync();
            using var image = await pending;
            Assert.NotNull(image);
            Assert.Equal("downloaded", await File.ReadAllTextAsync(image.Path, timeout.Token));
        }
        finally
        {
            await pipe.Writer.CompleteAsync();
            if (pending is not null)
            {
                (await pending)?.Dispose();
            }
            DeleteDirectory(directory);
        }
    }

    /// <summary>文件响应执行失败也会释放租约，使后续清理不会永久跳过图片。</summary>
    [Fact]
    public async Task FileResultReleasesLeaseWhenResponseExecutionFails()
    {
        var directory = CreateDirectoryPath();
        using var http = new AudienceArtworkHttpHandler();
        using var cache = CreateCache(http, directory);
        try
        {
            using var image = await cache.GetAsync("https://i0.hdslb.com/failure.png");
            Assert.NotNull(image);
            var result = new ArtworkFileResult(image);

            await Assert.ThrowsAnyAsync<Exception>(() => result.ExecuteAsync(new DefaultHttpContext()));

            Assert.Equal(2, (await cache.CleanupAsync(0)).DeletedFiles);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    /// <summary>图片文件响应写入尚未完成时仍持有租约，发送结束后缓存才可回收。</summary>
    [Fact]
    public async Task FileResultProtectsImageUntilResponseBodyFinishesWriting()
    {
        var directory = CreateDirectoryPath();
        using var http = new AudienceArtworkHttpHandler();
        using var cache = CreateCache(http, directory);
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 1, resumeWriterThreshold: 1));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var context = new DefaultHttpContext { RequestServices = services, RequestAborted = timeout.Token };
        context.Request.Method = "GET";
        context.Response.Body = pipe.Writer.AsStream(leaveOpen: true);
        Task? pending = null;
        try
        {
            using var image = await cache.GetAsync("https://i0.hdslb.com/sending.png", timeout.Token);
            Assert.NotNull(image);
            pending = new ArtworkFileResult(image, lastModified: true).ExecuteAsync(context);
            var read = await pipe.Reader.ReadAsync(timeout.Token);
            Assert.True(read.Buffer.Length > 0);

            Assert.False(pending.IsCompleted);
            Assert.Equal(0, (await cache.CleanupAsync(0, timeout.Token)).DeletedFiles);
            Assert.True(File.Exists(image.Path));
            pipe.Reader.AdvanceTo(read.Buffer.End);
            await pending.WaitAsync(timeout.Token);

            Assert.Equal(2, (await cache.CleanupAsync(0, timeout.Token)).DeletedFiles);
            Assert.False(File.Exists(image.Path));
        }
        finally
        {
            await pipe.Reader.CompleteAsync();
            if (pending is not null)
            {
                await pending.WaitAsync(timeout.Token);
            }
            await pipe.Writer.CompleteAsync();
            DeleteDirectory(directory);
        }
    }

    private static string CreateDirectoryPath() => Path.Combine(Path.GetTempPath(), "recorder-artwork-" + Guid.NewGuid().ToString("N"));

    private static ArtworkCache CreateCache(AudienceArtworkHttpHandler http, string directory) =>
        new(http, new AppPaths(directory, Path.Combine(directory, "records")));

    private static void DeleteDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}
