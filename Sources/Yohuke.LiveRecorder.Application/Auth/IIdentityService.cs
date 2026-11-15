using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Application.Auth;

/// <summary>本地身份、邮箱验证、邀请与外部授权的应用边界。</summary>
public interface IIdentityService
{
    /// <summary>验证密码并拒绝未审核或已停用账号。</summary>
    Task<UserProfile> AuthenticateAsync(LoginRequest request, CancellationToken cancellationToken = default);
    /// <summary>读取用户当前权限与审核状态。</summary>
    Task<UserProfile?> GetUserAsync(string id, CancellationToken cancellationToken = default);
    /// <summary>更新当前用户的界面语言，空值表示跟随全局默认。</summary>
    Task<UserProfile> SetLanguageAsync(string id, LanguagePreferenceRequest request, CancellationToken cancellationToken = default);
    /// <summary>保存当前用户在各个录播中共用的直播分析界面偏好。</summary>
    Task<UserProfile> SetAnalyticsPreferencesAsync(string id, AnalyticsPreferencesRequest request, CancellationToken cancellationToken = default);
    /// <summary>重验 Cookie 身份，仅返回当前仍有效的用户与角色。</summary>
    Task<UserProfile?> ValidateSessionAsync(string id, long sessionVersion = 0, CancellationToken cancellationToken = default);
    /// <summary>列出网站用户。</summary>
    Task<IReadOnlyList<UserProfile>> ListUsersAsync(CancellationToken cancellationToken = default);
    /// <summary>启用邮箱验证时，通过配置的邮件服务器发送单次验证码。</summary>
    Task SendEmailCodeAsync(EmailCodeRequest request, CancellationToken cancellationToken = default);
    /// <summary>按服务器配置校验注册要求并创建用户。</summary>
    Task<UserProfile> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default);
    /// <summary>将已验证外部身份按相同注册要求创建为本地用户。</summary>
    Task<UserProfile> CompleteOAuthRegistrationAsync(string ticket, OAuthRegistrationRequest request, CancellationToken cancellationToken = default);
    /// <summary>批准或拒绝用户。</summary>
    Task<UserProfile> SetApprovalAsync(string id, bool approved, CancellationToken cancellationToken = default);
    /// <summary>由有效管理员封禁或解封用户，并撤销被封禁用户的已有会话。</summary>
    Task<UserProfile> SetBannedAsync(string actorId, string id, bool banned, CancellationToken cancellationToken = default);
    /// <summary>修改角色并保护最后一名管理员。</summary>
    Task<UserProfile> SetRoleAsync(string id, string role, CancellationToken cancellationToken = default);
    /// <summary>修改用户的独立权限，管理员的有效权限保持完整。</summary>
    Task<UserProfile> SetPermissionsAsync(string id, PermissionSet permissions, CancellationToken cancellationToken = default);
    /// <summary>创建邀请码，仅本次返回明文。</summary>
    Task<InvitationSummary> CreateInvitationAsync(InvitationRequest request, string createdBy, CancellationToken cancellationToken = default);
    /// <summary>读取邀请码使用状态。</summary>
    Task<IReadOnlyList<InvitationSummary>> ListInvitationsAsync(CancellationToken cancellationToken = default);
    /// <summary>撤销邀请码。</summary>
    Task RevokeInvitationAsync(string id, CancellationToken cancellationToken = default);
    /// <summary>建立绑定浏览器的授权码和 PKCE 流程。</summary>
    Task<OAuthAuthorizationResult> BeginOAuthAsync(string providerId, string redirectUri, string browserBinding, CancellationToken cancellationToken = default);
    /// <summary>读取当前有效用户显式绑定的提供方标识。</summary>
    Task<IReadOnlyList<string>> ListExternalProvidersAsync(string userId, CancellationToken cancellationToken = default);
    /// <summary>为当前有效用户创建显式绑定流程。</summary>
    Task<OAuthAuthorizationResult> BeginOAuthBindingAsync(string userId, string providerId, string redirectUri, string browserBinding, CancellationToken cancellationToken = default);
    /// <summary>一次性消费 state，完成服务器端换码。</summary>
    Task<OAuthLoginResult> CompleteOAuthAsync(string providerId, string state, string code, string browserBinding, CancellationToken cancellationToken = default);
    /// <summary>完成授权并以当前 Cookie 身份约束绑定意图。</summary>
    Task<OAuthLoginResult> CompleteOAuthAsync(string providerId, string state, string code, string browserBinding, string? currentUserId, CancellationToken cancellationToken = default);
}
