using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>通过真实 Kestrel 验证匿名错误也遵循请求语言且不依赖数据库。</summary>
public sealed class LocalizedResponseHttpTests
{
    /// <summary>业务校验错误返回稳定错误码及对应语言。</summary>
    [Theory]
    [InlineData("zh-CN", "请输入有效的邮箱地址。")]
    [InlineData("en-US", "Enter a valid email address.")]
    [InlineData("ja-JP", "有効なメールアドレスを入力してください。")]
    [InlineData("fr-FR", "请输入有效的邮箱地址。")]
    public async Task AnonymousValidationUsesRequestedLanguage(string language, string expected)
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        var browser = fixture.NewBrowser();
        browser.Client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(language);
        await browser.RefreshCsrfAsync();
        using var response = await browser.SendAsync(HttpMethod.Post, "/api/auth/login", new { email = "invalid", password = "unused" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("AUTH_EMAIL_INVALID", problem!["code"]!.GetValue<string>());
        Assert.Equal(expected, problem["detail"]!.GetValue<string>());
        Assert.Equal(language == "fr-FR" ? "zh-CN" : language, response.Content.Headers.ContentLanguage.Single());
    }

    /// <summary>并发请求不能互相覆盖文化；权限及 CSRF 错误同样本地化。</summary>
    [Fact]
    public async Task ConcurrentRequestsKeepIndependentCultures()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        var english = fixture.NewBrowser();
        var japanese = fixture.NewBrowser();
        english.Client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US");
        japanese.Client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ja-JP");
        var responses = await Task.WhenAll(english.Client.GetAsync("/api/streamers"), japanese.Client.GetAsync("/api/streamers"));
        using var englishResponse = responses[0];
        using var japaneseResponse = responses[1];
        Assert.Equal(HttpStatusCode.Unauthorized, englishResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, japaneseResponse.StatusCode);
        var englishProblem = await englishResponse.Content.ReadFromJsonAsync<JsonObject>();
        var japaneseProblem = await japaneseResponse.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("Sign in to continue.", englishProblem!["detail"]!.GetValue<string>());
        Assert.Equal("ログインしてください。", japaneseProblem!["detail"]!.GetValue<string>());
        using var csrfResponse = await japanese.SendAsync(HttpMethod.Post, "/api/auth/login", new { email = "test@example.test", password = "unused" }, includeCsrf: false);
        var csrfProblem = await csrfResponse.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(HttpStatusCode.BadRequest, csrfResponse.StatusCode);
        Assert.Equal("REQUEST_CSRF_EXPIRED", csrfProblem!["code"]!.GetValue<string>());
        Assert.Equal("リクエストの検証が期限切れです。再読み込みしてお試しください。", csrfProblem["detail"]!.GetValue<string>());
    }
}
