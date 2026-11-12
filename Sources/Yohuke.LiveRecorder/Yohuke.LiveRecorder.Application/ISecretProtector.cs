namespace Yohuke.LiveRecorder.Application;

/// <summary>保护持久化凭据。</summary>
public interface ISecretProtector
{
    /// <summary>保护明文。</summary>
    string Protect(string value);
    /// <summary>解密受保护的内容。</summary>
    string Unprotect(string value);
}
