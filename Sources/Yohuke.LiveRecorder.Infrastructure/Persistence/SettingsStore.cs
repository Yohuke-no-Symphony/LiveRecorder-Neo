using System.Text.Json;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Auth;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Files;

namespace Yohuke.LiveRecorder.Infrastructure.Persistence;

/// <summary>从唯一设置文件读取全局参数，兼容一次性导入旧 Data/settings.json 或数据库设置。</summary>
public sealed class SettingsStore(StartupConfigurationStore configuration, IBootstrapStore bootstrap, IMongoContext context, IAppPaths paths) : ISettingsStore
{
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <inheritdoc />
    public async Task<ServerSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (configuration.HasSettings)
            {
                return configuration.ReadSettings();
            }
            if (!bootstrap.IsInstalled)
            {
                return new() { DefaultUserPermissions = PermissionCatalog.Defaults };
            }
            var legacyFile = Path.Combine(paths.DataDirectory, "settings.json");
            var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            ServerSettings? legacy = null;
            if (!string.Equals(legacyFile, configuration.ConfigFile, comparison) && File.Exists(legacyFile))
            {
                await using var stream = File.OpenRead(legacyFile);
                legacy = await JsonSerializer.DeserializeAsync<ServerSettings>(stream, JsonFiles.Options, cancellationToken)
                         ?? throw new InvalidDataException("旧全局设置无效。");
            }
            legacy ??= await LegacySettingsReader.ReadAsync(context.Database, cancellationToken);
            if (legacy is null)
            {
                throw new InvalidDataException("已安装实例缺少全局设置，请恢复 settings.json。");
            }
            await configuration.SaveSettingsAsync(legacy, cancellationToken);
            return configuration.ReadSettings();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(ServerSettings settings, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await configuration.SaveSettingsAsync(settings, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }
}
