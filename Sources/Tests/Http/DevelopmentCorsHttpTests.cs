using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>验证开发前端跨端口凭据访问、CSRF 与发布环境隔离。</summary>
public sealed class DevelopmentCorsHttpTests
{
    private const string CUSTOM_ORIGIN = "http://127.0.0.1:5884";

    [Theory]
    [InlineData("Development", "http://127.0.0.1:5784", true)]
    [InlineData("Development", "https://untrusted.example", false)]
    [InlineData("Production", "http://127.0.0.1:5784", false)]
    public async Task PreflightOnlyPermitsConfiguredDevelopmentOrigin(string environment, string origin, bool allowed)
    {
        await using var fixture = await HttpServerFixture.CreateAsync(environment, "--WebUI:DevelopmentOrigins:0", "http://127.0.0.1:5784");
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/auth/login");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "content-type,x-csrf-token");
        using var response = await fixture.Admin.Client.SendAsync(request);
        if (allowed)
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.Equal(origin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
            Assert.Equal("true", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Credentials")));
            var headers = string.Join(",", response.Headers.GetValues("Access-Control-Allow-Headers"));
            Assert.Contains("x-csrf-token", headers, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
            Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
        }
    }

    [Fact]
    public async Task ConfiguredFrontEndCanReadCsrfAndSubmitSetupWithoutBypassingValidation()
    {
        await using var fixture = await HttpServerFixture.CreateAsync("Development", "--WebUI:DevelopmentOrigins:0", CUSTOM_ORIGIN);
        fixture.Admin.Client.DefaultRequestHeaders.Add("Origin", CUSTOM_ORIGIN);
        await fixture.Admin.RefreshCsrfAsync();
        var request = fixture.SetupRequest() with { Email = "invalid" };
        using var invalid = await fixture.Admin.SendAsync(HttpMethod.Post, "/api/setup", request, includeCsrf: false);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(CUSTOM_ORIGIN, Assert.Single(invalid.Headers.GetValues("Access-Control-Allow-Origin")));
        using var valid = await fixture.Admin.SendAsync(HttpMethod.Post, "/api/setup", request);
        Assert.Equal(HttpStatusCode.BadRequest, valid.StatusCode);
        var problem = await valid.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("AUTH_EMAIL_INVALID", problem!["code"]!.GetValue<string>());
        Assert.Equal(CUSTOM_ORIGIN, Assert.Single(valid.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Equal("true", Assert.Single(valid.Headers.GetValues("Access-Control-Allow-Credentials")));
    }
}
