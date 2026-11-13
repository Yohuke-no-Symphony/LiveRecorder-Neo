using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Xunit;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>验证账号头像读取、管理员权限和图片来源限制。</summary>
public sealed class BilibiliAccountAvatarHttpTests
{
    [MongoHttpFact]
    public async Task AdminReceivesCachedImageWithItsContentType()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var account = new BilibiliAccount { Uid = 42, AvatarUrl = "https://i0.hdslb.com/account-avatar.png", ProtectedCredentials = "protected-account-credentials" };
        await fixture.Context.Collection<BilibiliAccount>("bilibili_accounts").InsertOneAsync(account);
        var image = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jB8UAAAAASUVORK5CYII=");
        await CacheImageAsync(fixture, account.AvatarUrl, image);

        using var response = await fixture.Admin.Client.GetAsync($"/api/admin/bilibili/accounts/{account.Id}/avatar");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(image, await response.Content.ReadAsByteArrayAsync());
    }

    [MongoHttpFact]
    public async Task AvatarRequiresAdminForAnonymousAndRegularUsers()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        var browser = fixture.NewBrowser();
        using (var anonymous = await browser.Client.GetAsync("/api/admin/bilibili/accounts/example/avatar"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }
        var user = await fixture.CreateUserAsync("avatar-viewer@example.test");
        await browser.LoginAsync(user.Email, HttpServerFixture.USER_PASSWORD);

        using var regularUser = await browser.Client.GetAsync("/api/admin/bilibili/accounts/example/avatar");

        Assert.Equal(HttpStatusCode.Forbidden, regularUser.StatusCode);
    }

    [MongoHttpFact]
    public async Task MissingAvatarAndDisallowedSourceReturnNotFound()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        using (var missing = await fixture.Admin.Client.GetAsync("/api/admin/bilibili/accounts/missing/avatar"))
        {
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
        var account = new BilibiliAccount { Uid = 42 };
        await fixture.Context.Collection<BilibiliAccount>("bilibili_accounts").InsertOneAsync(account);
        using (var empty = await fixture.Admin.Client.GetAsync($"/api/admin/bilibili/accounts/{account.Id}/avatar"))
        {
            Assert.Equal(HttpStatusCode.NotFound, empty.StatusCode);
        }
        account.AvatarUrl = "https://outside.example.test/avatar.png";
        await fixture.Context.Collection<BilibiliAccount>("bilibili_accounts").UpdateOneAsync(
            x => x.Id == account.Id, Builders<BilibiliAccount>.Update.Set(x => x.AvatarUrl, account.AvatarUrl));
        await CacheImageAsync(fixture, account.AvatarUrl, [1, 2, 3]);

        using var disallowed = await fixture.Admin.Client.GetAsync($"/api/admin/bilibili/accounts/{account.Id}/avatar?v=https%3A%2F%2Fi0.hdslb.com%2Fignored.png");

        Assert.Equal(HttpStatusCode.NotFound, disallowed.StatusCode);
    }

    private static async Task CacheImageAsync(HttpServerFixture fixture, string source, byte[] image)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
        var paths = fixture.App.Services.GetRequiredService<IAppPaths>();
        var root = Path.Combine(paths.CacheDirectory, "artwork");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, key + ".image");
        await File.WriteAllBytesAsync(path, image);
        await File.WriteAllTextAsync(path + ".type", "image/png");
    }
}
