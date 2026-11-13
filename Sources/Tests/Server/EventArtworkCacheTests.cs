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
        var cache = new ArtworkCache(http, new AppPaths(directory, Path.Combine(directory, "records")));
        try
        {
            var path = await cache.GetAsync("http://i0.hdslb.com/emote.png");

            Assert.NotNull(path);
            var request = Assert.Single(http.Requests);
            Assert.Equal("https", request.Source.Scheme);
            Assert.Equal("https://live.bilibili.com/", request.Referer);
            Assert.NotEmpty(request.UserAgent);
            Assert.Equal("image/png", await File.ReadAllTextAsync(path + ".type"));
            Assert.Equal("/emote.png", await File.ReadAllTextAsync(path));
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
        var cache = new ArtworkCache(http, new AppPaths(directory, Path.Combine(directory, "records")));

        Assert.Null(await cache.GetAsync(source));
        Assert.Empty(http.Requests);
        Assert.False(Directory.Exists(directory));
    }
}
