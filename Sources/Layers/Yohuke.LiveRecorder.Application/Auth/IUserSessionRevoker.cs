namespace Yohuke.LiveRecorder.Application.Auth;

/// <summary>在安全版本更新后撤销用户已有的实时连接。</summary>
public interface IUserSessionRevoker
{
    /// <summary>关闭版本低于当前安全版本的连接，保留更新后建立的会话。</summary>
    void Revoke(string userId, long sessionVersion);
}
