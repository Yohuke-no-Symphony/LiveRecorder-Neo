using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Yohuke.LiveRecorder.Http.Tests;

internal sealed class OAuthTestProvider : IAsyncDisposable
{
    private WebApplication app = null!;
    internal Uri Address { get; private set; } = null!;
    internal string ExpectedChallenge { get; set; } = "";

    internal static async Task<OAuthTestProvider> CreateAsync()
    {
        var provider = new OAuthTestProvider();
        var builder = WebApplication.CreateBuilder(["--urls", "http://127.0.0.1:0", "--environment", "Production", "--Logging:LogLevel:Default", "Critical"]);
        provider.app = builder.Build();
        provider.app.MapPost("/oauth2/token", async (HttpContext http) =>
        {
            var form = await http.Request.ReadFormAsync();
            var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"].ToString()))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return form["client_id"] == "http-client" && challenge == provider.ExpectedChallenge
                ? Results.Ok(new { access_token = "http-test-access-token", token_type = "Bearer" })
                : Results.BadRequest(new { error = "invalid_grant" });
        });
        provider.app.MapGet("/oauth2/userinfo", (HttpContext http) => http.Request.Headers.Authorization == "Bearer http-test-access-token"
            ? Results.Ok(new { sub = "http-external-subject", displayName = "External profile", email = "external@example.test", emailVerified = true })
            : Results.Unauthorized());
        try
        {
            await provider.app.StartAsync();
            provider.Address = new(provider.app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            return provider;
        }
        catch
        {
            await provider.app.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.StopAsync(timeout.Token);
        await app.DisposeAsync();
    }
}
