using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Auth;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Persistence;
using Yohuke.LiveRecorder.Server;
using Xunit;

namespace Yohuke.LiveRecorder.Http.Tests;

internal sealed class HttpServerFixture : IAsyncDisposable
{
    internal const string ADMIN_EMAIL = "admin@example.test";
    internal const string ADMIN_PASSWORD = "http-administrator-password";
    internal const string USER_PASSWORD = "http-viewer-password";
    private readonly List<HttpBrowser> browsers = [];
    private bool databaseTouched;
    private string HostEnvironment { get; init; } = "Production";
    private string[] AdditionalArguments { get; init; } = [];
    internal string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "live-recorder-http-tests", Guid.NewGuid().ToString("N"));
    internal string DatabaseName { get; } = "http_tests_" + Guid.NewGuid().ToString("N");
    internal string MongoConnectionString { get; } = Environment.GetEnvironmentVariable("LIVE_RECORDER_TEST_MONGO") ?? "mongodb://127.0.0.1:34784";
    internal string StoragePath => Path.Combine(DirectoryPath, "recordings");
    internal WebApplication App { get; private set; } = null!;
    internal HttpBrowser Admin { get; private set; } = null!;
    internal Uri Address { get; private set; } = null!;
    internal MongoContext Context => App.Services.GetRequiredService<MongoContext>();

    internal static async Task<HttpServerFixture> CreateAsync(string environment = "Production", params string[] additionalArguments)
    {
        var fixture = new HttpServerFixture { HostEnvironment = environment, AdditionalArguments = additionalArguments };
        try
        {
            await fixture.StartAsync();
            fixture.Admin = fixture.NewBrowser();
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    internal HttpBrowser NewBrowser(CookieContainer? cookies = null)
    {
        var browser = new HttpBrowser(Address, cookies);
        browsers.Add(browser);
        return browser;
    }

    internal async Task RestartAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await App.StopAsync(timeout.Token);
        await App.DisposeAsync();
        await StartAsync();
    }

    private async Task StartAsync()
    {
        App = ServerStartup.CreateApplication(["--urls", "http://127.0.0.1:0", "--DataDirectory", Path.Combine(DirectoryPath, "data"), "--environment", HostEnvironment, "--Logging:LogLevel:Default", "Critical", .. AdditionalArguments]);
        await App.StartAsync();
        var addresses = App.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        Address = new(addresses.Single());
    }

    internal async Task InstallAsync()
    {
        await Admin.RefreshCsrfAsync();
        databaseTouched = true;
        using var response = await Admin.SendAsync(HttpMethod.Post, "/api/setup", SetupRequest());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>();
        Assert.True(result!["installed"]!.GetValue<bool>());
    }

    internal object SetupRequest() => new
    {
        token = App.Services.GetRequiredService<BootstrapStore>().InstallationToken,
        userName = "HTTP 管理员",
        email = ADMIN_EMAIL,
        password = ADMIN_PASSWORD,
        mongoConnectionString = MongoConnectionString,
        databaseName = DatabaseName,
        storagePath = StoragePath
    };

    internal async Task<UserAccount> CreateUserAsync(string email, string status = "Active", string role = "User")
    {
        var user = new UserAccount { Email = email, NormalizedEmail = IdentityValidation.NormalizeEmail(email), DisplayName = email.Split('@')[0], Role = role, Status = status, EmailVerified = true };
        user.PasswordHash = App.Services.GetRequiredService<IPasswordHasher<UserAccount>>().HashPassword(user, USER_PASSWORD);
        await Context.Collection<UserAccount>("users").InsertOneAsync(user);
        return user;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var browser in browsers)
        {
            browser.Dispose();
        }
        try
        {
            if (App is not null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await App.StopAsync(timeout.Token);
                await App.DisposeAsync();
            }
        }
        finally
        {
            if (databaseTouched && DatabaseName.StartsWith("http_tests_", StringComparison.Ordinal) && DatabaseName.Length == 43)
            {
                await new MongoClient(MongoConnectionString).DropDatabaseAsync(DatabaseName);
            }
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, true);
            }
        }
    }
}
