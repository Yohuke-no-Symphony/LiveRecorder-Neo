using Microsoft.AspNetCore.DataProtection;

namespace Yohuke.LiveRecorder.Infrastructure.Configuration;

/// <summary>为可缓存的认证与防伪组件创建随实例快照解析的保护器。</summary>
public sealed class CurrentDataProtectionProvider(CurrentInstanceStorage storage) : IDataProtectionProvider
{
    /// <inheritdoc />
    public IDataProtector CreateProtector(string purpose) => new CurrentDataProtector(storage, purpose);
}
