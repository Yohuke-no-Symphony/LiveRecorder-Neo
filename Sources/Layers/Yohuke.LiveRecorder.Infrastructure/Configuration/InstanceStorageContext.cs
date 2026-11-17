using Microsoft.AspNetCore.DataProtection;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Infrastructure.Persistence;

namespace Yohuke.LiveRecorder.Infrastructure.Configuration;

/// <summary>绑定同一实例的路径、密钥提供者、安装配置与数据库的不可变快照。</summary>
public sealed class InstanceStorageContext(AppPaths paths, IDataProtectionProvider dataProtectionProvider, ISecretProtector secretProtector, BootstrapStore bootstrap, MongoContext mongo, bool isInstalled)
{
    public AppPaths Paths { get; } = paths;
    public IDataProtectionProvider DataProtectionProvider { get; } = dataProtectionProvider;
    public ISecretProtector SecretProtector { get; } = secretProtector;
    public BootstrapStore Bootstrap { get; } = bootstrap;
    public MongoContext Mongo { get; } = mongo;
    public bool IsInstalled { get; } = isInstalled;
}
