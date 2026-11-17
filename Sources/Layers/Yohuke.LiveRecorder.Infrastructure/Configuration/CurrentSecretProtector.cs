using Yohuke.LiveRecorder.Application;

namespace Yohuke.LiveRecorder.Infrastructure.Configuration;

/// <summary>使用当前实例密钥保护平台凭据与敏感设置。</summary>
public sealed class CurrentSecretProtector(CurrentInstanceStorage storage) : ISecretProtector
{
    /// <inheritdoc />
    public string Protect(string value) => storage.Current.SecretProtector.Protect(value);

    /// <inheritdoc />
    public string Unprotect(string value) => storage.Current.SecretProtector.Unprotect(value);
}
