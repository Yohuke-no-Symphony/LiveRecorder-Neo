using System.Net;
using Xunit;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>使用真实 HTTP 管线验证消息图片鉴权、交付与失败缓存策略。</summary>
public sealed class EventArtworkEndpointTests
{
    [Fact]
    public async Task AnonymousCannotReadAnyMessageArtwork()
    {
        await using var context = await EventArtworkHttpContext.CreateAsync();
        foreach (var route in new[] { "/api/audience/42/avatar", "/api/sessions/missing/events/1/avatar", "/api/sessions/missing/events/1/emoticons/0" })
        {
            using var response = await context.Client.GetAsync(route);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        Assert.Empty(context.Source.Calls);
    }

    [Fact]
    public async Task RegularViewerReceivesPrivateCachedAvatarWithActualImageType()
    {
        await using var context = await EventArtworkHttpContext.CreateAsync();
        context.Client.DefaultRequestHeaders.Add("X-Artwork-Test-User", "viewer");

        using var response = await context.Client.GetAsync("/api/audience/42/avatar");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(EventArtworkHttpContext.ImageBytes, await response.Content.ReadAsByteArrayAsync());
        Assert.True(response.Headers.CacheControl!.Private);
        Assert.Equal(TimeSpan.FromMinutes(5), response.Headers.CacheControl.MaxAge);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        using var repeated = await context.Client.GetAsync("/api/audience/42/avatar");
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        Assert.Equal(1, context.Source.Calls[42]);
    }

    [Fact]
    public async Task MissingAvatarAndInvalidUidReturnNonCacheableNotFound()
    {
        await using var context = await EventArtworkHttpContext.CreateAsync();
        context.Client.DefaultRequestHeaders.Add("X-Artwork-Test-User", "viewer");
        foreach (var uid in new[] { 43, 43, 0 })
        {
            using var response = await context.Client.GetAsync($"/api/audience/{uid}/avatar?url=https://outside.example/image.png");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.True(response.Headers.CacheControl!.NoStore);
        }
        Assert.Equal(1, context.Source.Calls[43]);
        Assert.False(context.Source.Calls.ContainsKey(0));
    }
}
