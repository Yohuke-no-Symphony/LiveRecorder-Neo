using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Auth;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Auth;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Persistence;
using Yohuke.LiveRecorder.Infrastructure.Persistence.Migrations;

namespace Yohuke.LiveRecorder.Identity.Tests;

internal sealed class IdentityFixture : IAsyncDisposable
{
    internal const string ADMIN_PASSWORD = "administrator-test-password";
    internal const string USER_PASSWORD = "registered-user-password";
    internal string DatabaseName { get; } = "identity_tests_" + Guid.NewGuid().ToString("N");
    internal string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "live-recorder-identity-tests", Guid.NewGuid().ToString("N"));
    internal string ConnectionString { get; } = Environment.GetEnvironmentVariable("LIVE_RECORDER_TEST_MONGO")!;
    internal CapturingEmailSender Email { get; } = new();
    internal CapturingSessionRevoker SessionRevoker { get; } = new();
    internal TestHttpClientFactory Http { get; } = new();
    internal ISecretProtector Protector { get; private set; } = null!;
    internal AppPaths Paths { get; private set; } = null!;
    internal BootstrapStore Bootstrap { get; private set; } = null!;
    internal MongoContext Context { get; private set; } = null!;
    internal ISettingsStore Settings { get; private set; } = null!;
    internal InstallationService Installation { get; private set; } = null!;
    internal IdentityService Identity { get; private set; } = null!;
    private ServiceProvider? services;

    internal static async Task<IdentityFixture> CreateAsync()
    {
        var fixture = new IdentityFixture();
        try
        {
            var factory = new InstanceStorageFactory();
            var initial = factory.CreateInitial(Path.Combine(fixture.DirectoryPath, "data"), Path.Combine(fixture.DirectoryPath, "records"), false);
            var current = new CurrentInstanceStorage(initial);
            fixture.Paths = initial.Paths;
            var startup = new StartupConfigurationStore(Path.Combine(fixture.DirectoryPath, "startup.json"));
            fixture.Bootstrap = initial.Bootstrap;
            fixture.Context = initial.Mongo;
            fixture.Protector = initial.SecretProtector;
            fixture.Settings = new SettingsStore(startup, new CurrentBootstrapStore(current), new CurrentMongoContext(current), new CurrentAppPaths(current));
            var hasher = new PasswordHasher<UserAccount>();
            fixture.services = new ServiceCollection().AddLogging().AddMigrations().BuildServiceProvider();
            fixture.Installation = new(current, factory, startup, fixture.Settings, hasher,
                fixture.services.GetRequiredService<MigrationManager>());
            await fixture.Installation.InstallAsync(fixture.InstallRequest());
            fixture.Paths = current.Current.Paths;
            fixture.Bootstrap = current.Current.Bootstrap;
            fixture.Context = current.Current.Mongo;
            fixture.Protector = current.Current.SecretProtector;
            var avatarCache = new OAuthAvatarCache(fixture.Context, fixture.Http, NullLogger<OAuthAvatarCache>.Instance);
            fixture.Identity = new(fixture.Context, fixture.Settings, fixture.Email, fixture.Protector, hasher, fixture.Http, fixture.SessionRevoker, avatarCache);
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    internal InstallRequest InstallRequest() => new(Bootstrap.InstallationToken, "admin@example.test", ADMIN_PASSWORD, "管理员", ConnectionString, DatabaseName, Paths.DataDirectory, Paths.RecordsDirectory);

    internal async Task EnableRegistrationAsync()
    {
        var settings = await Settings.GetAsync();
        settings.RegistrationEnabled = true;
        await Settings.SaveAsync(settings);
    }

    internal async Task<string> SendCodeAsync(string email)
    {
        await Identity.SendEmailCodeAsync(new(email));
        return Email.CodeFor(email);
    }

    public async ValueTask DisposeAsync()
    {
        if (services is not null)
        {
            await services.DisposeAsync();
        }
        if (DatabaseName.StartsWith("identity_tests_", StringComparison.Ordinal) && DatabaseName.Length == 47)
        {
            await new MongoClient(ConnectionString).DropDatabaseAsync(DatabaseName);
        }
        if (Directory.Exists(DirectoryPath))
        {
            Directory.Delete(DirectoryPath, true);
        }
    }
}
