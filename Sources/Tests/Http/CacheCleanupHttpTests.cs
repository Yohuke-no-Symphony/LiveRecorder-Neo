using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Yohuke.LiveRecorder.Infrastructure.Storage;
using Xunit;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>独立于数据库验证缓存清理的真实 HTTP 授权、CSRF、选项与删除范围。</summary>
public sealed class CacheCleanupHttpTests
{
    private const string CLEANUP_PATH = "/api/storage/cache/cleanup";

    /// <summary>匿名和普通成员在参数绑定与缓存删除之前即被拒绝。</summary>
    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("User", HttpStatusCode.Forbidden)]
    public async Task CleanupRequiresAdministratorBeforeAccessingCache(string? role, HttpStatusCode expected)
    {
        await using var context = await CacheCleanupHttpContext.CreateAsync();
        var files = await SeedAsync(context);
        var browser = context.NewBrowser(role);

        using var response = await browser.SendAsync(HttpMethod.Post, CLEANUP_PATH,
            new { categories = new[] { "playback" }, minimumUnusedMinutes = 0 }, includeCsrf: false);

        Assert.Equal(expected, response.StatusCode);
        AssertFilesRemain(files.Values.SelectMany(paths => paths));
    }

    /// <summary>管理员缺失或伪造 CSRF Token 时不删除任何缓存。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrInvalidCsrfRejectsAdministratorWithoutDeletingFiles(bool invalidToken)
    {
        await using var context = await CacheCleanupHttpContext.CreateAsync();
        var files = await SeedAsync(context);
        var browser = context.NewBrowser("Admin");
        await browser.RefreshCsrfAsync();
        if (invalidToken)
        {
            browser.Client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", "invalid-token");
        }

        using var response = await browser.SendAsync(HttpMethod.Post, CLEANUP_PATH,
            new { categories = new[] { "playback" }, minimumUnusedMinutes = 0 }, includeCsrf: false);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("REQUEST_CSRF_EXPIRED", problem!["code"]!.GetValue<string>());
        AssertFilesRemain(files.Values.SelectMany(paths => paths));
    }

    /// <summary>有效管理员请求仅删除指定缓存类别并返回实际释放容量，始终保留录播文件。</summary>
    [Theory]
    [InlineData("playback", 1L, 7L)]
    [InlineData("artwork", 2L, 20L)]
    [InlineData("analyticsContributions", 1L, 13L)]
    public async Task ValidCsrfCleansOnlySelectedCategory(string category, long deletedFiles, long deletedBytes)
    {
        await using var context = await CacheCleanupHttpContext.CreateAsync();
        var files = await SeedAsync(context);
        var browser = context.NewBrowser("Admin");
        await browser.RefreshCsrfAsync();

        using var response = await browser.SendAsync(HttpMethod.Post, CLEANUP_PATH,
            new { categories = new[] { category }, minimumUnusedMinutes = 0 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.NotNull(result);
        Assert.Equal(deletedFiles, result["deletedFiles"]!.GetValue<long>());
        Assert.Equal(deletedBytes, result["deletedBytes"]!.GetValue<long>());
        Assert.Equal(0L, result["skippedEntries"]!.GetValue<long>());
        Assert.Equal(0L, result["failedEntries"]!.GetValue<long>());
        var categoryResult = Assert.Single(result["categories"]!.AsArray())!.AsObject();
        Assert.Equal(category, categoryResult["category"]!.GetValue<string>());
        Assert.Equal(deletedFiles, categoryResult["deletedFiles"]!.GetValue<long>());
        Assert.Equal(deletedBytes, categoryResult["deletedBytes"]!.GetValue<long>());
        Assert.Equal(0L, categoryResult["skippedEntries"]!.GetValue<long>());
        Assert.Equal(0L, categoryResult["failedEntries"]!.GetValue<long>());
        Assert.All(files[category], path => Assert.False(File.Exists(path), path));
        AssertFilesRemain(files.Where(item => item.Key != category).SelectMany(item => item.Value));
    }

    /// <summary>空类别、未知类别与越界时间全部先校验，拒绝部分执行。</summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"categories\":null,\"minimumUnusedMinutes\":0}")]
    [InlineData("{\"categories\":[],\"minimumUnusedMinutes\":0}")]
    [InlineData("{\"categories\":[\"playback\",\"unknown\"],\"minimumUnusedMinutes\":0}")]
    [InlineData("{\"categories\":[\"playback\"],\"minimumUnusedMinutes\":-1}")]
    [InlineData("{\"categories\":[\"playback\"],\"minimumUnusedMinutes\":525601}")]
    public async Task InvalidOptionsLeaveEveryCategoryUntouched(string json)
    {
        await using var context = await CacheCleanupHttpContext.CreateAsync();
        var files = await SeedAsync(context);
        var browser = context.NewBrowser("Admin");
        await browser.RefreshCsrfAsync();

        using var response = await browser.SendAsync(HttpMethod.Post, CLEANUP_PATH, JsonNode.Parse(json));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertFilesRemain(files.Values.SelectMany(paths => paths));
    }

    /// <summary>真实宿主注册缓存服务和 POST 端点，合并后的端点策略仅允许管理员。</summary>
    [Fact]
    public async Task ProductionHostComposesCleanupAndRequiresAdministrator()
    {
        await using var fixture = await HttpServerFixture.CreateAsync("Production", "--TranscodingControl:Enabled", "false");
        var services = fixture.App.Services;
        Assert.IsType<CacheCleanupService>(services.GetRequiredService<CacheCleanupService>());
        var routes = ((IEndpointRouteBuilder)fixture.App).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>();
        var endpoint = Assert.Single(routes, route => route.RoutePattern.RawText == CLEANUP_PATH);
        Assert.Equal(HttpMethods.Post, Assert.Single(endpoint.Metadata.GetRequiredMetadata<HttpMethodMetadata>().HttpMethods));
        Assert.Null(endpoint.Metadata.GetMetadata<IAllowAnonymous>());
        var provider = services.GetRequiredService<IAuthorizationPolicyProvider>();
        var inherited = await AuthorizationPolicy.CombineAsync(provider, endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
        Assert.NotNull(inherited);
        var policy = AuthorizationPolicy.Combine(new[] { inherited }.Concat(endpoint.Metadata.GetOrderedMetadata<AuthorizationPolicy>()));
        var authorization = services.GetRequiredService<IAuthorizationService>();
        var viewer = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "viewer"), new Claim(ClaimTypes.Role, "User")], "Integration"));
        var administrator = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "admin"), new Claim(ClaimTypes.Role, "Admin")], "Integration"));

        Assert.False((await authorization.AuthorizeAsync(new ClaimsPrincipal(new ClaimsIdentity()), null, policy)).Succeeded);
        Assert.False((await authorization.AuthorizeAsync(viewer, null, policy)).Succeeded);
        Assert.True((await authorization.AuthorizeAsync(administrator, null, policy)).Succeeded);
    }

    private static async Task<Dictionary<string, string[]>> SeedAsync(CacheCleanupHttpContext context)
    {
        var root = context.Paths.CacheDirectory;
        var playback = await WriteAsync(Path.Combine(root, "playback", new string('a', 64), "generation", "playlist.m3u8"), 7);
        var artwork = await WriteAsync(Path.Combine(root, "artwork", new string('b', 64) + ".image"), 11);
        var artworkType = await WriteAsync(artwork + ".type", 9);
        var contribution = await WriteAsync(Path.Combine(root, "analytics-contributions", new string('c', 64) + ".json"), 13);
        var recording = await WriteAsync(Path.Combine(context.Paths.RecordsDirectory, "room", "session", "source", "source.flv"), 17);
        return new()
        {
            ["playback"] = [playback],
            ["artwork"] = [artwork, artworkType],
            ["analyticsContributions"] = [contribution],
            ["recordings"] = [recording]
        };
    }

    private static async Task<string> WriteAsync(string path, int length)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, new byte[length]);
        var modified = DateTime.UtcNow.AddHours(-2);
        File.SetLastWriteTimeUtc(path, modified);
        Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(path)!, modified);
        return path;
    }

    private static void AssertFilesRemain(IEnumerable<string> paths)
    {
        Assert.All(paths, path => Assert.True(File.Exists(path), path));
    }
}
