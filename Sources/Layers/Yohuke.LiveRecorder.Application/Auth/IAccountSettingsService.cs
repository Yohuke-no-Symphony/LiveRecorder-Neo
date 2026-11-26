namespace Yohuke.LiveRecorder.Application.Auth;

/// <summary>当前会话的账号资料、密码和邮箱设置。</summary>
public interface IAccountSettingsService
{
    /// <summary>读取账号支持的操作及正在进行的换绑验证。</summary>
    Task<AccountSettingsResponse> GetSettingsAsync(string userId, long sessionVersion, CancellationToken cancellationToken = default);

    /// <summary>更新当前账号的显示名称。</summary>
    Task<UserProfile> ChangeDisplayNameAsync(string userId, long sessionVersion, DisplayNameChangeRequest request, CancellationToken cancellationToken = default);

    /// <summary>验证现有本地密码并撤销旧会话。</summary>
    Task ChangePasswordAsync(string userId, long sessionVersion, PasswordChangeRequest request, CancellationToken cancellationToken = default);

    /// <summary>使用当前本地密码或仍有效的换绑授权发送验证码。</summary>
    Task<PendingEmailChangeResponse> RequestEmailChangeAsync(string userId, long sessionVersion, EmailChangeRequest request, CancellationToken cancellationToken = default);

    /// <summary>在启动 OAuth 换绑前验证账号和目标邮箱。</summary>
    Task<string> ValidateOAuthEmailChangeAsync(string userId, long sessionVersion, string email, CancellationToken cancellationToken = default);

    /// <summary>根据同一外部身份的换绑授权向新邮箱发送验证码。</summary>
    Task RequestOAuthEmailChangeAsync(OAuthEmailChangeAuthorization authorization, CancellationToken cancellationToken = default);

    /// <summary>一次性确认新邮箱并撤销旧会话。</summary>
    Task ConfirmEmailChangeAsync(string userId, long sessionVersion, EmailChangeConfirmRequest request, CancellationToken cancellationToken = default);
}
