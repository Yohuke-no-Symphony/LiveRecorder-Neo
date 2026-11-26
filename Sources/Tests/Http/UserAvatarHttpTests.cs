using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>验证自定义头像通过真实媒体工具写入实例目录并可清空。</summary>
public sealed class UserAvatarHttpTests
{
    [MongoHttpFact]
    public async Task UploadReencodesTheImageAndClearRemovesThePublishedFile()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        var viewer = await fixture.CreateUserAsync("avatar-viewer@example.test");
        var browser = fixture.NewBrowser();
        await browser.LoginAsync(viewer.Email, HttpServerFixture.USER_PASSWORD);
        var image = Convert.FromBase64String("/9j/4AAQSkZJRgABAgAAAQABAAD//gAPTGF2YzYzLjEuMTAyAP/bAEMACAQEBAQEBQUFBQUFBgYGBgYGBgYGBgYGBgcHBwgICAcHBwYGBwcICAgICQkJCAgICAkJCgoKDAwLCw4ODhERFP/EAE0AAQEAAAAAAAAAAAAAAAAAAAAHAQEBAQAAAAAAAAAAAAAAAAAABAYQAQAAAAAAAAAAAAAAAAAAAAARAQAAAAAAAAAAAAAAAAAAAAD/wAARCAAwACADASIAAhEAAxEA/9oADAMBAAIRAxEAPwCHAN+kAAAAAAAAAAf/2Q==");
        using var rejected = await UploadAsync(browser, [0xff, 0xd8, 0xff, 0, 0]);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);

        using var saved = await UploadAsync(browser, image);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var profile = await saved.Content.ReadFromJsonAsync<JsonObject>();
        var url = profile!["avatarUrl"]!.GetValue<string>();
        Assert.StartsWith($"/api/auth/avatars/{viewer.Id}?v=", url);
        using var anonymous = await fixture.NewBrowser().Client.GetAsync(url);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var avatar = await browser.Client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, avatar.StatusCode);
        Assert.Equal("image/jpeg", avatar.Content.Headers.ContentType!.MediaType);
        var compressed = await avatar.Content.ReadAsByteArrayAsync();
        Assert.InRange(compressed.Length, 1, 128 * 1024);
        Assert.False(image.AsSpan().SequenceEqual(compressed));
        var file = Assert.Single(Directory.EnumerateFiles(Path.Combine(fixture.DataDirectory, "avatars"), "*", SearchOption.AllDirectories),
            path => File.ReadAllBytes(path).AsSpan().SequenceEqual(compressed));

        using var cleared = await browser.SendAsync(HttpMethod.Delete, "/api/auth/me/avatar");
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Empty((await cleared.Content.ReadFromJsonAsync<JsonObject>())!["avatarUrl"]!.GetValue<string>());
        Assert.False(File.Exists(file));
        using var oldUrl = await browser.Client.GetAsync(url);
        Assert.Equal(HttpStatusCode.NotFound, oldUrl.StatusCode);
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpBrowser browser, byte[] contents)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/auth/me/avatar") { Content = new ByteArrayContent(contents) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        request.Headers.Add("X-CSRF-TOKEN", browser.CsrfToken);
        return await browser.Client.SendAsync(request);
    }
}
