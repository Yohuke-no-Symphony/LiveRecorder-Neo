using System.Security.Claims;
using System.Globalization;
using Microsoft.AspNetCore.Authentication.Cookies;
using Yohuke.LiveRecorder.Application.Auth;

namespace Yohuke.LiveRecorder.Infrastructure.Auth;

/// <summary>为登录和会话重验建立一致的身份及权限声明。</summary>
public static class UserPrincipalFactory
{
    public const string SESSION_VERSION_CLAIM = "lr.session-version";

    /// <summary>根据当前用户资料建立 Cookie 身份，管理员始终具备全部权限。</summary>
    public static ClaimsPrincipal Create(UserProfile user)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id),
            new(ClaimTypes.Name, user.DisplayName),
            new(ClaimTypes.Email, user.Email),
            new(ClaimTypes.Role, user.Role),
            new(SESSION_VERSION_CLAIM, user.SessionVersion.ToString(CultureInfo.InvariantCulture))
        };
        var permissions = user.Role == "Admin" ? PermissionCatalog.All : user.Permissions;
        foreach (var permission in permissions.Where(PermissionCatalog.All.Contains))
        {
            claims.Add(new(UserPermissionNames.CLAIM_TYPE, permission));
        }
        return new(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    /// <summary>读取会话安全版本，兼容旧 Cookie 的缺失字段并拒绝无效声明。</summary>
    public static long? GetSessionVersion(ClaimsPrincipal? principal)
    {
        if (principal is null)
        {
            return null;
        }
        var value = principal.FindFirstValue(SESSION_VERSION_CLAIM);
        if (value is null)
        {
            return 0;
        }
        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var version) && version >= 0 ? version : null;
    }
}
