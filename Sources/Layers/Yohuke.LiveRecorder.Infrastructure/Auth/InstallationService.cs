using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using MongoDB.Bson;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Auth;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Persistence;
using Yohuke.LiveRecorder.Infrastructure.Persistence.Migrations;

namespace Yohuke.LiveRecorder.Infrastructure.Auth;

/// <summary>在所选目录验证并完成新装或恢复，成功后一次性发布实例存储。</summary>
public sealed class InstallationService(CurrentInstanceStorage current, InstanceStorageFactory factory, StartupConfigurationStore configuration,
    ISettingsStore settingsStore, IPasswordHasher<UserAccount> passwordHasher, MigrationManager migrations) : IInstallationService
{
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <inheritdoc />
    public bool IsInstalled => current.IsInstalled;

    /// <inheritdoc />
    public async Task InstallAsync(InstallRequest request, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var expected = current.Current;
            if (current.IsInstalled)
            {
                throw new InfrastructureException(MessageCode.SETUP_ALREADY_INSTALLED, 409);
            }
            if (string.IsNullOrWhiteSpace(request.InstallationToken) || !IdentityToken.EqualsHash(IdentityToken.Hash(request.InstallationToken), IdentityToken.Hash(expected.Bootstrap.InstallationToken)))
            {
                throw new InfrastructureException(MessageCode.SETUP_TOKEN_INVALID, 403);
            }
            if (!Enum.IsDefined(request.Mode) || string.IsNullOrWhiteSpace(request.DatabaseName) || request.DatabaseName.Length > 63 || request.DatabaseName.IndexOfAny(['/', '\\', '.', ' ', '"', '$', '\0']) >= 0)
            {
                throw new InfrastructureException(MessageCode.SETUP_CONFIGURATION_INVALID);
            }
            var email = "";
            var name = "";
            var password = request.AdminPassword ?? "";
            if (request.Mode == InstallationMode.New)
            {
                email = IdentityValidation.NormalizeEmail(request.AdminEmail ?? "");
                name = IdentityValidation.NormalizeDisplayName(request.AdminDisplayName ?? "");
                IdentityValidation.ValidatePassword(password);
            }
            configuration.ValidatePaths(request.DataDirectory, request.RecordsDirectory);
            var candidate = factory.CreateCandidate(configuration.ResolveDataDirectory(request.DataDirectory), configuration.ResolveRecordsDirectory(request.RecordsDirectory));
            var paths = candidate.Paths;
            var bootstrap = candidate.Bootstrap;
            var retry = request.Mode == InstallationMode.New
                        && bootstrap.Current is { Installed: false, Mode: InstallationMode.New } previous
                        && previous.MongoConnectionString == request.MongoConnectionString
                        && previous.DatabaseName == request.DatabaseName;
            var legacyFile = Path.Combine(paths.DataDirectory, "settings.json");
            var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var hasLegacySettings = !string.Equals(legacyFile, configuration.ConfigFile, comparison) && File.Exists(legacyFile);
            if (request.Mode == InstallationMode.New && !retry && (File.Exists(paths.BootstrapFile) || hasLegacySettings || Directory.Exists(paths.RecordsDirectory) && Directory.EnumerateFileSystemEntries(paths.RecordsDirectory).Any()))
            {
                throw new InfrastructureException(MessageCode.SETUP_EXISTING_CONFIGURATION, 409);
            }
            var mongoSettings = MongoClientSettings.FromConnectionString(request.MongoConnectionString);
            mongoSettings.ServerSelectionTimeout = TimeSpan.FromSeconds(8);
            mongoSettings.ConnectTimeout = TimeSpan.FromSeconds(8);
            var database = new MongoClient(mongoSettings).GetDatabase(request.DatabaseName);
            await database.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: cancellationToken);
            if (request.Mode == InstallationMode.New && !retry)
            {
                using var collections = await database.ListCollectionNamesAsync(cancellationToken: cancellationToken);
                if ((await collections.ToListAsync(cancellationToken)).Any(value => !value.StartsWith("system.", StringComparison.Ordinal)))
                {
                    throw new InfrastructureException(MessageCode.SETUP_DATABASE_NOT_EMPTY, 409);
                }
            }
            var users = database.GetCollection<UserAccount>("users");
            var existing = request.Mode == InstallationMode.New
                ? await users.Find(x => x.NormalizedEmail == email && x.Role == "Admin" && x.Status == "Active").FirstOrDefaultAsync(cancellationToken)
                : null;
            if (request.Mode == InstallationMode.Restore)
            {
                if (!await users.Find(x => x.Role == "Admin" && x.Status == "Active").AnyAsync(cancellationToken))
                {
                    throw new InfrastructureException(MessageCode.SETUP_RESTORE_ADMIN_MISSING, 409);
                }
            }
            else if (retry && await users.Find(x => x.Role == "Admin").AnyAsync(cancellationToken))
            {
                if (existing is null || passwordHasher.VerifyHashedPassword(existing, existing.PasswordHash, password) == PasswordVerificationResult.Failed)
                {
                    throw new InfrastructureException(MessageCode.SETUP_RESTORE_ADMIN_INVALID, 403);
                }
            }
            var settings = configuration.HasSettings ? await settingsStore.GetAsync(cancellationToken) : null;
            if (settings is null && (request.Mode == InstallationMode.Restore || retry) && hasLegacySettings)
            {
                await using var stream = File.OpenRead(legacyFile);
                settings = await JsonSerializer.DeserializeAsync<ServerSettings>(stream, JsonFiles.Options, cancellationToken)
                           ?? throw new InvalidDataException("旧全局设置无效。");
            }
            if (request.Mode == InstallationMode.Restore)
            {
                settings ??= await LegacySettingsReader.ReadAsync(database, cancellationToken)
                             ?? throw new InfrastructureException(MessageCode.SETUP_RESTORE_SETTINGS_MISSING, 409);
            }
            settings ??= new() { DefaultUserPermissions = PermissionCatalog.Defaults };
            await RequireOriginalKeysAsync(database, settings, candidate.SecretProtector, cancellationToken);
            if (!string.IsNullOrWhiteSpace(request.FfmpegPath))
            {
                settings.FfmpegPath = request.FfmpegPath;
            }
            if (!string.IsNullOrWhiteSpace(request.FfprobePath))
            {
                settings.FfprobePath = request.FfprobePath;
            }
            await VerifyToolAsync(settings.FfmpegPath, cancellationToken);
            await VerifyToolAsync(settings.FfprobePath, cancellationToken);
            candidate = factory.CreateForInstallationCommit(candidate);
            paths = candidate.Paths;
            bootstrap = candidate.Bootstrap;
            var probe = Path.Combine(paths.RecordsDirectory, $".installation-{Guid.NewGuid():N}");
            await using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
                await stream.WriteAsync(new byte[] { 1 }, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            var installed = new BootstrapConfiguration
            {
                Installed = false,
                Mode = request.Mode,
                MongoConnectionString = request.MongoConnectionString,
                DatabaseName = request.DatabaseName
            };
            await bootstrap.SaveAsync(installed, cancellationToken);
            if (request.Mode == InstallationMode.New && existing is null)
            {
                await users.Indexes.CreateOneAsync(new CreateIndexModel<UserAccount>(Builders<UserAccount>.IndexKeys.Ascending(x => x.NormalizedEmail), new CreateIndexOptions { Unique = true }), cancellationToken: cancellationToken);
                var user = new UserAccount { Email = email, NormalizedEmail = email, DisplayName = name, EmailVerified = true, Role = "Admin", Status = "Active" };
                user.PasswordHash = passwordHasher.HashPassword(user, password);
                await users.InsertOneAsync(user, cancellationToken: cancellationToken);
            }
            if (request.Mode == InstallationMode.New)
            {
                await migrations.InitializeNewAsync(database, paths, cancellationToken);
            }
            else
            {
                await migrations.RunAsync(database, paths, cancellationToken);
            }
            installed.Installed = true;
            await configuration.CommitInstallationAsync(request.DataDirectory, request.RecordsDirectory, settings, token => bootstrap.SaveAsync(installed, token), cancellationToken);
            current.PublishInstalled(expected, candidate);
            current.MarkReady(current.Current);
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task RequireOriginalKeysAsync(IMongoDatabase database, ServerSettings settings, ISecretProtector protector, CancellationToken cancellationToken)
    {
        var secrets = settings.OAuthProviders.Select(provider => provider.ClientSecret).Append(settings.Smtp.Password).Append(settings.Ai.ApiKey);
        var credentials = await database.GetCollection<BilibiliAccount>("bilibili_accounts").Find(FilterDefinition<BilibiliAccount>.Empty).Project(account => account.ProtectedCredentials).ToListAsync(cancellationToken);
        try
        {
            foreach (var value in secrets.Concat(credentials).Where(value => !string.IsNullOrEmpty(value)))
            {
                protector.Unprotect(value);
            }
        }
        catch (CryptographicException)
        {
            throw new InfrastructureException(MessageCode.SETUP_RESTORE_KEYS_MISSING, 409);
        }
    }

    private static async Task VerifyToolAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var start = new ProcessStartInfo(path) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("-version");
            using var process = Process.Start(start) ?? throw new InfrastructureException(MessageCode.SETUP_MEDIA_TOOL_START_FAILED);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var error = process.StandardError.ReadToEndAsync(deadline.Token);
            try
            {
                await process.WaitForExitAsync(deadline.Token);
            }
            catch
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                }
                throw;
            }
            await Task.WhenAll(output, error);
            if (process.ExitCode != 0)
            {
                throw new InfrastructureException(MessageCode.SETUP_MEDIA_TOOL_VERIFICATION_FAILED);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InfrastructureException(MessageCode.SETUP_MEDIA_TOOL_VERIFICATION_FAILED);
        }
        catch (Exception error) when (error is not InfrastructureException && error is not OperationCanceledException)
        {
            throw new InfrastructureException(MessageCode.SETUP_MEDIA_TOOL_NOT_FOUND);
        }
    }
}
