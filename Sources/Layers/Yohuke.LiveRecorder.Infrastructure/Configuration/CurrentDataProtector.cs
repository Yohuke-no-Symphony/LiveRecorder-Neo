using Microsoft.AspNetCore.DataProtection;

namespace Yohuke.LiveRecorder.Infrastructure.Configuration;

/// <summary>保留用途链，在每次加解密时选择同一已发布实例的标准保护器。</summary>
public sealed class CurrentDataProtector : IDataProtector
{
    private readonly CurrentInstanceStorage storage;
    private readonly string[] purposes;

    /// <summary>创建绑定初始用途但不缓存某个实例密钥提供者的保护器。</summary>
    public CurrentDataProtector(CurrentInstanceStorage storage, string purpose) : this(storage, [purpose])
    {
        ArgumentNullException.ThrowIfNull(purpose);
    }

    private CurrentDataProtector(CurrentInstanceStorage storage, string[] purposes)
    {
        this.storage = storage;
        this.purposes = purposes;
    }

    /// <inheritdoc />
    public IDataProtector CreateProtector(string purpose)
    {
        ArgumentNullException.ThrowIfNull(purpose);
        return new CurrentDataProtector(storage, [.. purposes, purpose]);
    }

    /// <inheritdoc />
    public byte[] Protect(byte[] plaintext) => Resolve().Protect(plaintext);

    /// <inheritdoc />
    public byte[] Unprotect(byte[] protectedData) => Resolve().Unprotect(protectedData);

    private IDataProtector Resolve()
    {
        var provider = storage.Current.DataProtectionProvider;
        var protector = provider.CreateProtector(purposes[0]);
        for (var index = 1; index < purposes.Length; index++)
        {
            protector = protector.CreateProtector(purposes[index]);
        }
        return protector;
    }
}
