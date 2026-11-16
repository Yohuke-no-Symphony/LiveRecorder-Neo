using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application.Auth;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Auth;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Persistence;
using Yohuke.LiveRecorder.Server;
using Yohuke.LiveRecorder.Server.Contracts;
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
    private string? dataDirectory;
    private string? recordsDirectory;
    private string? CertificatePath { get; init; }
    private string? CertificateSha256 { get; init; }
    private IAppPaths? activePaths;
    internal string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "live-recorder-http-tests", Guid.NewGuid().ToString("N"));
    internal string DatabaseName { get; } = "http_tests_" + Guid.NewGuid().ToString("N");
    internal string MongoConnectionString { get; } = Environment.GetEnvironmentVariable("LIVE_RECORDER_TEST_MONGO") ?? "mongodb://127.0.0.1:34784";
    internal string ConfigFile => Path.Combine(DirectoryPath, "startup.json");
    internal string DataDirectory => activePaths?.DataDirectory ?? dataDirectory ?? Path.Combine(DirectoryPath, "Data");
    internal string StoragePath => activePaths?.RecordsDirectory ?? recordsDirectory ?? Path.Combine(DataDirectory, "records");
    internal string CachePath => Path.Combine(DataDirectory, "cache");
    internal WebApplication App { get; private set; } = null!;
    internal HttpBrowser Admin { get; private set; } = null!;
    internal Uri Address { get; private set; } = null!;
    internal IMongoContext Context => App.Services.GetRequiredService<IMongoContext>();

    internal static Task<HttpServerFixture> CreateAsync(string environment = "Production", params string[] additionalArguments) =>
        CreateFixtureAsync(new HttpServerFixture { HostEnvironment = environment, AdditionalArguments = additionalArguments }, true);

    internal static Task<HttpServerFixture> CreateWithoutConfigurationAsync() =>
        CreateFixtureAsync(new HttpServerFixture(), false);

    internal static Task<HttpServerFixture> CreateHttpsAsync(string certificatePath, string certificateSha256, params string[] additionalArguments) =>
        CreateFixtureAsync(new HttpServerFixture
        {
            CertificatePath = certificatePath,
            CertificateSha256 = certificateSha256,
            AdditionalArguments = additionalArguments
        }, true);

    private static async Task<HttpServerFixture> CreateFixtureAsync(HttpServerFixture fixture, bool configure)
    {
        try
        {
            if (configure)
            {
                Directory.CreateDirectory(fixture.DirectoryPath);
                await File.WriteAllTextAsync(fixture.ConfigFile, JsonSerializer.Serialize(new { DataDirectory = fixture.DataDirectory, RecordsDirectory = fixture.StoragePath }));
            }
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
        var browser = new HttpBrowser(Address, cookies, CertificateSha256);
        browsers.Add(browser);
        return browser;
    }

    internal async Task RestartAsync(bool removeLocalSettings = false)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await App.StopAsync(timeout.Token);
        await App.DisposeAsync();
        if (removeLocalSettings)
        {
            await RemoveGlobalSettingsAsync();
        }
        await StartAsync();
        Admin = NewBrowser();
    }

    internal async Task RestartFromCopiedDataAsync(bool updateConfiguration = true)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await App.StopAsync(timeout.Token);
        await App.DisposeAsync();
        var target = Path.Combine(DirectoryPath, "restored-data");
        var targetRecords = Path.Combine(DirectoryPath, "restored-records");
        var sourceRecords = StoragePath;
        foreach (var file in Directory.EnumerateFiles(DataDirectory, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(DataDirectory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
        foreach (var file in Directory.EnumerateFiles(sourceRecords, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(targetRecords, Path.GetRelativePath(sourceRecords, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
        Directory.CreateDirectory(targetRecords);
        dataDirectory = target;
        recordsDirectory = targetRecords;
        if (updateConfiguration)
        {
            await new StartupConfigurationStore(ConfigFile).SaveAsync(target, targetRecords);
        }
        await StartAsync();
    }

    internal async Task RestartWithoutStartupConfigurationAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await App.StopAsync(timeout.Token);
        await App.DisposeAsync();
        File.Delete(ConfigFile);
        await StartAsync();
        Admin = NewBrowser();
        await Admin.RefreshCsrfAsync();
    }

    internal async Task ReopenSetupAsync(bool preserveSettings = true, bool preserveKeys = true)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await App.StopAsync(timeout.Token);
        await App.DisposeAsync();
        File.Delete(Path.Combine(DataDirectory, "bootstrap.json"));
        if (!preserveSettings)
        {
            await RemoveGlobalSettingsAsync();
        }
        if (!preserveKeys)
        {
            Directory.Delete(Path.Combine(DataDirectory, "keys"), true);
        }
        await StartAsync();
        Admin = NewBrowser();
        await Admin.RefreshCsrfAsync();
    }

    private async Task StartAsync()
    {
        activePaths = null;
        var configuration = new StartupConfigurationStore(ConfigFile);
        dataDirectory = configuration.ResolvedDataDirectory;
        recordsDirectory = configuration.ResolvedRecordsDirectory;
        var transport = CertificatePath is null
            ? new[] { "--urls", "http://127.0.0.1:0" }
            : new[] { "--urls", "https://127.0.0.1:0", "--Kestrel:Certificates:Default:Path", CertificatePath };
        App = ServerStartup.CreateApplication([.. transport, "--config", ConfigFile, "--environment", HostEnvironment,
            "--Logging:LogLevel:Default", "Critical", "--TranscodingControl:Enabled", "false", .. AdditionalArguments]);
        activePaths = App.Services.GetRequiredService<IAppPaths>();
        await App.StartAsync();
        var addresses = App.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        Address = new(addresses.Single());
    }

    internal async Task InstallAsync(SetupRequest? request = null)
    {
        await Admin.RefreshCsrfAsync();
        databaseTouched = true;
        using var response = await Admin.SendAsync(HttpMethod.Post, "/api/setup", request ?? SetupRequest());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>();
        Assert.True(result!["installed"]!.GetValue<bool>());
        await Admin.RefreshCsrfAsync();
    }

    internal SetupRequest SetupRequest(InstallationMode mode = InstallationMode.New, string? password = null) => new(
        App.Services.GetRequiredService<IBootstrapStore>().InstallationToken,
        mode == InstallationMode.New ? "HTTP 管理员" : null,
        mode == InstallationMode.New ? ADMIN_EMAIL : null,
        mode == InstallationMode.New ? password ?? ADMIN_PASSWORD : null,
        MongoConnectionString,
        DatabaseName,
        DataDirectory,
        StoragePath,
        mode);

    internal void TrackDatabase() => databaseTouched = true;

    internal InstanceStorageContext PrepareCommittedStorage()
    {
        var factory = new InstanceStorageFactory();
        return factory.CreateForInstallationCommit(factory.CreateCandidate(DataDirectory, StoragePath));
    }

    private async Task RemoveGlobalSettingsAsync()
    {
        var configuration = new StartupConfigurationStore(ConfigFile);
        await File.WriteAllTextAsync(ConfigFile, JsonSerializer.Serialize(new
        {
            DataDirectory = configuration.DataDirectory,
            RecordsDirectory = configuration.RecordsDirectory
        }));
        File.Delete(Path.Combine(DataDirectory, "settings.json"));
    }

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
