using Microsoft.AspNetCore.DataProtection;
using Yohuke.LiveRecorder.Infrastructure.Persistence;

namespace Yohuke.LiveRecorder.Infrastructure.Configuration;

/// <summary>为启动实例和隔离的安装候选分别创建标准密钥提供者与本地存储。</summary>
public sealed class InstanceStorageFactory
{
    private const string APPLICATION_NAME = "Yohuke.LiveRecorder";

    /// <summary>未安装时使用纯内存保护器；只为已经验证的已安装实例修复目录并启用持久化密钥。</summary>
    public InstanceStorageContext CreateInitial(string resolvedDataDirectory, string resolvedRecordsDirectory, bool isConfigured)
    {
        var paths = new AppPaths(resolvedDataDirectory, resolvedRecordsDirectory);
        if (!isConfigured || !File.Exists(paths.BootstrapFile))
        {
            return CreateEphemeral(paths);
        }
        var provider = CreateProvider(paths, false);
        var protector = new DataProtectionSecretProtector(provider);
        var bootstrap = new BootstrapStore(paths, protector);
        if (!bootstrap.IsInstalled)
        {
            return CreateEphemeral(paths, bootstrap);
        }
        var verified = new InstanceStorageContext(paths, provider, protector, bootstrap, new MongoContext(bootstrap), true);
        return CreateForInstallationCommit(verified);
    }

    /// <summary>只读验证安装候选的现有密钥与配置，不创建目录、生成密钥或复制其他实例密钥。</summary>
    public InstanceStorageContext CreateCandidate(string resolvedDataDirectory, string resolvedRecordsDirectory)
    {
        var paths = new AppPaths(resolvedDataDirectory, resolvedRecordsDirectory);
        var provider = CreateProvider(paths, false);
        var protector = new DataProtectionSecretProtector(provider);
        var bootstrap = new BootstrapStore(paths, protector, allowRecovery: true);
        return new(paths, provider, protector, bootstrap, new MongoContext(bootstrap), false);
    }

    /// <summary>验证完成后创建提交目录与可持久化保护器，沿用内存中的已验证配置和令牌，不重新读取磁盘。</summary>
    public InstanceStorageContext CreateForInstallationCommit(InstanceStorageContext candidate)
    {
        candidate.Paths.EnsureDirectories();
        var provider = CreateProvider(candidate.Paths, true);
        var protector = new DataProtectionSecretProtector(provider);
        var bootstrap = candidate.Bootstrap.WithProtector(protector);
        return new(candidate.Paths, provider, protector, bootstrap, new MongoContext(bootstrap), candidate.IsInstalled);
    }

    private static InstanceStorageContext CreateEphemeral(AppPaths paths, BootstrapStore? validatedBootstrap = null)
    {
        var provider = new EphemeralDataProtectionProvider();
        var protector = new DataProtectionSecretProtector(provider);
        var bootstrap = validatedBootstrap?.WithProtector(protector) ?? new BootstrapStore(paths, protector, loadExisting: false);
        return new(paths, provider, protector, bootstrap, new MongoContext(bootstrap), false);
    }

    private static IDataProtectionProvider CreateProvider(AppPaths paths, bool writable)
    {
        return DataProtectionProvider.Create(new DirectoryInfo(paths.KeysDirectory), builder =>
        {
            builder.SetApplicationName(APPLICATION_NAME);
            if (!writable)
            {
                builder.DisableAutomaticKeyGeneration();
                builder.AddKeyManagementOptions(options => options.XmlRepository = new ReadonlyFileKeyRepository(new DirectoryInfo(paths.KeysDirectory)));
            }
            if (OperatingSystem.IsWindows())
            {
                builder.ProtectKeysWithDpapi();
            }
        });
    }
}
