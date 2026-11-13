using System.Text.Json;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Files;

namespace Yohuke.LiveRecorder.Infrastructure.Configuration;

/// <summary>在唯一设置文件中原子保存目录与全局参数，运行目录只在安装完成或重启时切换。</summary>
public sealed class StartupConfigurationStore
{
    private static readonly HashSet<string> SETTINGS_KEYS = JsonSerializer.SerializeToElement(new ServerSettings(), JsonFiles.Options)
        .EnumerateObject().Select(property => property.Name).Where(name => name is not ("id" or "createdAtUtc")).ToHashSet(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim gate = new(1, 1);
    private StartupConfiguration configuration;
    private bool isConfigured;

    public string ConfigFile { get; }
    public string DataDirectory => Volatile.Read(ref configuration).DataDirectory;
    public string RecordsDirectory => RecordsPath(Volatile.Read(ref configuration));
    public string ResolvedDataDirectory => ResolveDataDirectory(DataDirectory);
    public string ResolvedRecordsDirectory => ResolveRecordsDirectory(RecordsDirectory);
    public bool IsConfigured => Volatile.Read(ref isConfigured);
    public bool HasSettings => Volatile.Read(ref configuration).AdditionalProperties.Keys.Any(SETTINGS_KEYS.Contains);
    private static StringComparison PathComparison => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>读取指定设置文件；不存在时使用相对文件目录的 Data 和 Data/records，并保持未配置状态。</summary>
    public StartupConfigurationStore(string? configFile = null)
    {
        ConfigFile = Path.GetFullPath(configFile ?? Path.Combine(AppContext.BaseDirectory, "settings.json"));
        if (Directory.Exists(ConfigFile))
        {
            throw new InvalidDataException("配置入口必须是 JSON 文件。");
        }
        isConfigured = File.Exists(ConfigFile);
        configuration = isConfigured ? ReadFile() : new();
        ValidatePaths(configuration.DataDirectory, RecordsPath(configuration));
    }

    /// <summary>返回独立配置快照，保证一组目录来自同一次保存。</summary>
    public StartupConfiguration GetSnapshot() => Clone(Volatile.Read(ref configuration));

    /// <summary>从唯一配置快照读取全局参数，不包含另一份设置文件或主机绝对路径。</summary>
    public ServerSettings ReadSettings()
    {
        return JsonSerializer.Deserialize<ServerSettings>(JsonSerializer.Serialize(Volatile.Read(ref configuration), JsonFiles.Options), JsonFiles.Options)
               ?? throw new InvalidDataException("全局设置无效。");
    }

    /// <summary>以配置文件所在目录为基准解析数据目录。</summary>
    public string ResolveDataDirectory(string dataDirectory)
    {
        var resolved = ResolveDirectory(dataDirectory);
        if (string.Equals(Path.TrimEndingDirectorySeparator(resolved), Path.GetPathRoot(resolved), PathComparison)
            || new[] { "bootstrap.json", "installation-token" }.Any(name => IsWithin(ConfigFile, Path.Combine(resolved, name)))
            || new[] { "keys", "cache" }.Any(name => IsWithin(ConfigFile, Path.Combine(resolved, name))))
        {
            throw new InfrastructureException(MessageCode.STARTUP_CONFIGURATION_PATH_CONFLICT);
        }
        return resolved;
    }

    /// <summary>以配置文件所在目录为基准解析录像目录，配置文件不可位于媒体目录内。</summary>
    public string ResolveRecordsDirectory(string recordsDirectory)
    {
        var resolved = ResolveDirectory(recordsDirectory);
        if (IsWithin(ConfigFile, resolved))
        {
            throw new InfrastructureException(MessageCode.STARTUP_CONFIGURATION_PATH_CONFLICT);
        }
        return resolved;
    }

    /// <summary>验证独立目录的私有文件边界，允许尚不存在的目录和默认 Data/records 布局。</summary>
    public void ValidatePaths(string dataDirectory, string recordsDirectory)
    {
        var data = ResolveDataDirectory(dataDirectory);
        var records = ResolveRecordsDirectory(recordsDirectory);
        if (IsWithin(data, records)
            || new[] { "bootstrap.json", "installation-token" }.Any(name => IsWithin(records, Path.Combine(data, name)))
            || new[] { "keys", "cache" }.Any(name => IsWithin(records, Path.Combine(data, name)) || IsWithin(Path.Combine(data, name), records)))
        {
            throw new InfrastructureException(MessageCode.STARTUP_CONFIGURATION_PATH_CONFLICT);
        }
        RequireDirectory(data);
        RequireDirectory(records);
    }

    /// <summary>只更新两个目录，保留所有全局参数和未知字段，不切换当前运行实例。</summary>
    public Task SaveAsync(string dataDirectory, string recordsDirectory, CancellationToken cancellationToken = default) =>
        WriteAsync(dataDirectory, recordsDirectory, null, cancellationToken);

    /// <summary>只更新全局参数，保留最新配置中的目录和未知字段。</summary>
    public Task SaveSettingsAsync(ServerSettings settings, CancellationToken cancellationToken = default) =>
        WriteAsync(null, null, settings, cancellationToken);

    /// <summary>安装成功时将所选目录与全局参数作为一次完整配置提交。</summary>
    public Task SaveForInstallationAsync(string dataDirectory, string recordsDirectory, ServerSettings settings, CancellationToken cancellationToken = default) =>
        WriteAsync(dataDirectory, recordsDirectory, settings, cancellationToken);

    /// <summary>提交唯一配置和安装标记；安装标记写入失败时恢复原配置，成功后才发布配置快照。</summary>
    public Task CommitInstallationAsync(string dataDirectory, string recordsDirectory, ServerSettings settings, Func<CancellationToken, Task> finalize, CancellationToken cancellationToken = default) =>
        WriteAsync(dataDirectory, recordsDirectory, settings, cancellationToken, finalize);

    private async Task WriteAsync(string? dataDirectory, string? recordsDirectory, ServerSettings? settings, CancellationToken cancellationToken, Func<CancellationToken, Task>? finalize = null)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var previousText = File.Exists(ConfigFile) ? await File.ReadAllTextAsync(ConfigFile, cancellationToken) : null;
            var snapshot = previousText is null
                ? Clone(configuration)
                : Clone(JsonSerializer.Deserialize<StartupConfiguration>(previousText, JsonFiles.Options) ?? throw new InvalidDataException("设置文件无效。"));
            snapshot.DataDirectory = dataDirectory ?? snapshot.DataDirectory;
            snapshot.RecordsDirectory = recordsDirectory ?? RecordsPath(snapshot);
            ValidatePaths(snapshot.DataDirectory, snapshot.RecordsDirectory);
            if (settings is not null)
            {
                foreach (var property in JsonSerializer.SerializeToElement(settings, JsonFiles.Options).EnumerateObject())
                {
                    snapshot.AdditionalProperties[property.Name] = property.Value.Clone();
                }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigFile)!);
            await PrivateFiles.WriteAllTextAsync(ConfigFile, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(JsonFiles.Options) { WriteIndented = true }), cancellationToken);
            if (finalize is not null)
            {
                try
                {
                    await finalize(cancellationToken);
                }
                catch
                {
                    if (previousText is null)
                    {
                        File.Delete(ConfigFile);
                    }
                    else
                    {
                        await PrivateFiles.WriteAllTextAsync(ConfigFile, previousText, CancellationToken.None);
                    }
                    throw;
                }
            }
            Volatile.Write(ref configuration, snapshot);
            Volatile.Write(ref isConfigured, true);
        }
        finally
        {
            gate.Release();
        }
    }

    private StartupConfiguration ReadFile()
    {
        var loaded = JsonSerializer.Deserialize<StartupConfiguration>(File.ReadAllText(ConfigFile), JsonFiles.Options)
                     ?? throw new InvalidDataException("设置文件无效。");
        return Clone(loaded);
    }

    private static StartupConfiguration Clone(StartupConfiguration value) => new()
    {
        DataDirectory = value.DataDirectory,
        RecordsDirectory = RecordsPath(value),
        AdditionalProperties = new(value.AdditionalProperties, StringComparer.OrdinalIgnoreCase)
    };

    private static string RecordsPath(StartupConfiguration value) => value.RecordsDirectory ?? Path.Combine(value.DataDirectory, "records");

    private string ResolveDirectory(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InfrastructureException(MessageCode.SETTINGS_DATA_DIRECTORY_INVALID);
        }
        try
        {
            var resolved = Path.GetFullPath(value, Path.GetDirectoryName(ConfigFile)!);
            if (IsWithin(resolved, ConfigFile))
            {
                throw new InfrastructureException(MessageCode.STARTUP_CONFIGURATION_PATH_CONFLICT);
            }
            return resolved;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InfrastructureException(MessageCode.SETTINGS_DATA_DIRECTORY_INVALID);
        }
    }

    private static bool IsWithin(string path, string directory)
    {
        var root = Path.TrimEndingDirectorySeparator(directory);
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return string.Equals(path, root, PathComparison) || path.StartsWith(prefix, PathComparison);
    }

    private static void RequireDirectory(string directory)
    {
        for (var current = directory; current is not null; current = Path.GetDirectoryName(current))
        {
            if (File.Exists(current))
            {
                throw new InfrastructureException(MessageCode.SETTINGS_DATA_DIRECTORY_INVALID);
            }
            if (Directory.Exists(current))
            {
                return;
            }
        }
    }
}
