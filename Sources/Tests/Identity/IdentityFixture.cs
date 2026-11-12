using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Auth;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Auth;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Persistence;

namespace Yohuke.LiveRecorder.Identity.Tests;

internal sealed class IdentityFixture : IAsyncDisposable
{
    internal const string ADMIN_PASSWORD = "administrator-test-password";
    internal const string USER_PASSWORD = "registered-user-password";
    internal string DatabaseName { get; } = "identity_tests_" + Guid.NewGuid().ToString("N");
    internal string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "live-recorder-identity-tests", Guid.NewGuid().ToString("N"));
    internal string ConnectionString { get; } = Environment.GetEnvironmentVariable("LIVE_RECORDER_TEST_MONGO")!;
    internal CapturingEmailSender Email { get; } = new();
    internal TestHttpClientFactory Http { get; } = new();
    internal ISecretProtector Protector { get; } = new DataProtectionSecretProtector(new EphemeralDataProtectionProvider());
    internal BootstrapStore Bootstrap { get; private set; } = null!;
    internal MongoContext Context { get; private set; } = null!;
    internal ISettingsStore Settings { get; private set; } = null!;
    internal InstallationService Installation { get; private set; } = null!;
    internal IdentityService Identity { get; private set; } = null!;

    internal static async Task<IdentityFixture> CreateAsync()
    {
        var fixture = new IdentityFixture();
        try
        {
            var paths = new AppPaths(Path.Combine(fixture.DirectoryPath, "data"));
            fixture.Bootstrap = new(paths, fixture.Protector);
            fixture.Context = new(fixture.Bootstrap);
            fixture.Settings = new SettingsStore(fixture.Context, fixture.Bootstrap);
            var hasher = new PasswordHasher<UserAccount>();
            fixture.Installation = new(fixture.Bootstrap, fixture.Settings, hasher);
            await fixture.Installation.InstallAsync(fixture.InstallRequest());
            fixture.Identity = new(fixture.Context, fixture.Settings, fixture.Email, fixture.Protector, hasher, fixture.Http);
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    internal InstallRequest InstallRequest() => new(Bootstrap.InstallationToken, "admin@example.test", ADMIN_PASSWORD, "管理员", Path.Combine(DirectoryPath, "recordings"), ConnectionString, DatabaseName);

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
