using System.Security.Cryptography;
using System.Text.Json;
using Yohuke.LiveRecorder.Application;

namespace Yohuke.LiveRecorder.Infrastructure.Configuration;

/// <summary>原子保存受保护的安装信息与一次性安装令牌。</summary>
public sealed class BootstrapStore : IBootstrapStore
{
    private readonly IAppPaths paths;
    private readonly ISecretProtector protector;
    private readonly SemaphoreSlim gate = new(1, 1);
    public BootstrapConfiguration? Current { get; private set; }
    public bool IsInstalled => Current?.Installed == true;
    public string InstallationToken { get; }

    /// <summary>只读加载现有配置或生成内存安装令牌，不创建任何实例文件。</summary>
    public BootstrapStore(IAppPaths paths, ISecretProtector protector, bool allowRecovery = false, bool loadExisting = true)
    {
        this.paths = paths;
        this.protector = protector;
        var file = paths.BootstrapFile;
        if (loadExisting && File.Exists(file))
        {
            try
            {
                Current = JsonSerializer.Deserialize<BootstrapConfiguration>(protector.Unprotect(File.ReadAllText(file)))
                          ?? throw new InvalidDataException("安装配置无效。");
            }
            catch (Exception error) when (allowRecovery && error is CryptographicException or JsonException or InvalidDataException)
            {
                Current = null;
            }
        }
        var tokenFile = paths.InstallationTokenFile;
        InstallationToken = loadExisting && File.Exists(tokenFile) ? File.ReadAllText(tokenFile).Trim() : Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    }

    private BootstrapStore(IAppPaths paths, ISecretProtector protector, string installationToken, BootstrapConfiguration? current)
    {
        this.paths = paths;
        this.protector = protector;
        InstallationToken = installationToken;
        Current = current is null ? null : JsonSerializer.Deserialize<BootstrapConfiguration>(JsonSerializer.Serialize(current))!;
    }

    /// <summary>在显式安装提交时创建数据目录并原子保存令牌和安装阶段。</summary>
    public async Task SaveAsync(BootstrapConfiguration configuration, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var snapshot = JsonSerializer.Deserialize<BootstrapConfiguration>(JsonSerializer.Serialize(configuration))!;
            AppPaths.EnsureDataDirectory(paths.DataDirectory);
            if (!File.Exists(paths.InstallationTokenFile))
            {
                await PrivateFiles.WriteAllTextAsync(paths.InstallationTokenFile, InstallationToken, cancellationToken);
            }
            await PrivateFiles.WriteAllTextAsync(paths.BootstrapFile, protector.Protect(JsonSerializer.Serialize(snapshot)), cancellationToken);
            Current = snapshot;
        }
        finally
        {
            gate.Release();
        }
    }

    internal BootstrapStore WithProtector(ISecretProtector secretProtector)
    {
        return new(paths, secretProtector, InstallationToken, Current);
    }
}
