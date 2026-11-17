using Microsoft.AspNetCore.DataProtection;
using Yohuke.LiveRecorder.Application;

namespace Yohuke.LiveRecorder.Infrastructure.Configuration;

/// <summary>使用持久化密钥保护配置与平台凭据。</summary>
public sealed class DataProtectionSecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    private readonly IDataProtector protector = provider.CreateProtector("Yohuke.LiveRecorder.Secrets.v1");
    /// <inheritdoc />
    public string Protect(string value) => protector.Protect(value);
    /// <inheritdoc />
    public string Unprotect(string value) => protector.Unprotect(value);
}
