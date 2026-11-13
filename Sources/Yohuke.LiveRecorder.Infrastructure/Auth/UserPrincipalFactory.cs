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
        var permissions = user.Role == "Admin" ? PermissionCatalog.All : user.Permissions;
        foreach (var permission in permissions.Where(PermissionCatalog.All.Contains))
        {
            claims.Add(new(UserPermissionNames.CLAIM_TYPE, permission));
        }
        return new(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }
}
