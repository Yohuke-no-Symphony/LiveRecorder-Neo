using Yohuke.LiveRecorder.Application.Localization;
using Microsoft.AspNetCore.Identity;
using MongoDB.Bson;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Auth;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Configuration;

namespace Yohuke.LiveRecorder.Infrastructure.Auth;

/// <summary>验证数据库和磁盘后初始化管理员，支持未完成安装的安全重试。</summary>
public sealed class InstallationService(BootstrapStore bootstrap, ISettingsStore settingsStore, IPasswordHasher<UserAccount> passwordHasher) : IInstallationService
{
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <inheritdoc />
    public bool IsInstalled => bootstrap.IsInstalled;

    /// <inheritdoc />
    public async Task InstallAsync(InstallRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.InstallationToken) || !IdentityToken.EqualsHash(IdentityToken.Hash(request.InstallationToken), IdentityToken.Hash(bootstrap.InstallationToken)))
        {
            throw new InfrastructureException(MessageCode.SETUP_TOKEN_INVALID, 403);
        }
        var email = IdentityValidation.NormalizeEmail(request.AdminEmail);
        var name = IdentityValidation.NormalizeDisplayName(request.AdminDisplayName);
        IdentityValidation.ValidatePassword(request.AdminPassword);
        if (!Path.IsPathFullyQualified(request.StoragePath) || string.IsNullOrWhiteSpace(request.DatabaseName) || request.DatabaseName.Length > 63 || request.DatabaseName.IndexOfAny(['/', '\\', '.', ' ', '"', '$', '\0']) >= 0)
        {
            throw new InfrastructureException(MessageCode.SETUP_CONFIGURATION_INVALID);
        }
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (bootstrap.IsInstalled)
            {
                throw new InfrastructureException(MessageCode.SETUP_ALREADY_INSTALLED, 409);
            }
            var mongoSettings = MongoClientSettings.FromConnectionString(request.MongoConnectionString);
            mongoSettings.ServerSelectionTimeout = TimeSpan.FromSeconds(8);
            mongoSettings.ConnectTimeout = TimeSpan.FromSeconds(8);
            var database = new MongoClient(mongoSettings).GetDatabase(request.DatabaseName);
            await database.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: cancellationToken);
            var storagePath = Path.GetFullPath(request.StoragePath);
            Directory.CreateDirectory(storagePath);
            var probe = Path.Combine(storagePath, $".installation-{Guid.NewGuid():N}");
            await using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
                await stream.WriteAsync(new byte[] { 1 }, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            var configuration = new BootstrapConfiguration
            {
                Installed = false,
                MongoConnectionString = request.MongoConnectionString,
                DatabaseName = request.DatabaseName,
                StoragePath = storagePath
            };
            await bootstrap.SaveAsync(configuration, cancellationToken);
            var users = database.GetCollection<UserAccount>("users");
            await users.Indexes.CreateOneAsync(new CreateIndexModel<UserAccount>(Builders<UserAccount>.IndexKeys.Ascending(x => x.NormalizedEmail), new CreateIndexOptions { Unique = true }), cancellationToken: cancellationToken);
            var existing = await users.Find(x => x.Role == "Admin").FirstOrDefaultAsync(cancellationToken);
            if (existing is not null && existing.NormalizedEmail != email)
            {
                throw new InfrastructureException(MessageCode.SETUP_EXISTING_ADMIN, 409);
            }
            if (existing is null)
            {
                var user = new UserAccount { Email = email, NormalizedEmail = email, DisplayName = name, EmailVerified = true, Role = "Admin", Status = "Active" };
                user.PasswordHash = passwordHasher.HashPassword(user, request.AdminPassword);
                await users.InsertOneAsync(user, cancellationToken: cancellationToken);
            }
            else if (passwordHasher.VerifyHashedPassword(existing, existing.PasswordHash, request.AdminPassword) == PasswordVerificationResult.Failed)
            {
                throw new InfrastructureException(MessageCode.SETUP_EXISTING_ADMIN_PASSWORD_MISMATCH, 409);
            }
            await settingsStore.SaveAsync(new ServerSettings { StoragePath = storagePath }, cancellationToken);
            configuration.Installed = true;
            await bootstrap.SaveAsync(configuration, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }
}
