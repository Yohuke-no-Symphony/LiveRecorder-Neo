using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Yohuke.LiveRecorder.Application.Auth;

namespace Yohuke.LiveRecorder.Infrastructure.Auth;

/// <summary>为登录和会话重验建立一致的身份及权限声明。</summary>
public static class UserPrincipalFactory
{
    /// <summary>根据当前用户资料建立 Cookie 身份，管理员始终具备全部权限。</summary>
    public static ClaimsPrincipal Create(UserProfile user)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id),
            new(ClaimTypes.Name, user.DisplayName),
            new(ClaimTypes.Email, user.Email),
            new(ClaimTypes.Role, user.Role)
        };
        if (user.Role == "Admin" || user.Permissions.ManageRecordings)
        {
            claims.Add(new(UserPermissionNames.CLAIM_TYPE, UserPermissionNames.MANAGE_RECORDINGS));
        }
        if (user.Role == "Admin" || user.Permissions.ViewAnalytics)
        {
            claims.Add(new(UserPermissionNames.CLAIM_TYPE, UserPermissionNames.VIEW_ANALYTICS));
        }
        if (user.Role == "Admin" || user.Permissions.PurgeRecordings)
        {
            claims.Add(new(UserPermissionNames.CLAIM_TYPE, UserPermissionNames.PURGE_RECORDINGS));
        }
        return new(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }
}
