using System.Security.Cryptography;
using System.Text.Json;
using Yohuke.LiveRecorder.Application;

namespace Yohuke.LiveRecorder.Infrastructure.Configuration;

/// <summary>原子保存受保护的安装信息与一次性安装令牌。</summary>
public sealed class BootstrapStore
{
    private readonly AppPaths paths;
    private readonly ISecretProtector protector;
    private readonly SemaphoreSlim gate = new(1, 1);
    public BootstrapConfiguration? Current { get; private set; }
    public bool IsInstalled => Current?.Installed == true;
    public string InstallationToken { get; }

    /// <summary>读取已安装配置，配置损坏时拒绝回退为新安装。</summary>
    public BootstrapStore(AppPaths paths, ISecretProtector protector)
    {
        this.paths = paths;
        this.protector = protector;
        var file = Path.Combine(paths.DataDirectory, "bootstrap.json");
        if (File.Exists(file))
        {
            Current = JsonSerializer.Deserialize<BootstrapConfiguration>(protector.Unprotect(File.ReadAllText(file)))
                      ?? throw new InvalidDataException("安装配置无效。");
        }
        var tokenFile = Path.Combine(paths.DataDirectory, "installation-token");
        InstallationToken = File.Exists(tokenFile) ? File.ReadAllText(tokenFile).Trim() : Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        if (!IsInstalled && !File.Exists(tokenFile))
        {
            File.WriteAllText(tokenFile, InstallationToken);
            RestrictFile(tokenFile);
        }
    }

    /// <summary>以同卷替换方式保存安装阶段。</summary>
    public async Task SaveAsync(BootstrapConfiguration configuration, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var file = Path.Combine(paths.DataDirectory, "bootstrap.json");
            var temporary = file + ".tmp";
            await File.WriteAllTextAsync(temporary, protector.Protect(JsonSerializer.Serialize(configuration)), cancellationToken);
            RestrictFile(temporary);
            File.Move(temporary, file, true);
            Current = configuration;
        }
        finally
        {
            gate.Release();
        }
    }

    private static void RestrictFile(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
