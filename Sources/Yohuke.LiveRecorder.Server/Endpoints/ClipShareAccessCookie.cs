using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Yohuke.LiveRecorder.Infrastructure.Auth;

namespace Yohuke.LiveRecorder.Server.Endpoints;

internal sealed class ClipShareAccessCookie(IDataProtectionProvider dataProtection)
{
    private const string COOKIE_NAME = "lr.clip-share";
    private static readonly TimeSpan VALIDITY = TimeSpan.FromMinutes(30);
    private readonly IDataProtector protector = dataProtection.CreateProtector("Yohuke.LiveRecorder.ClipShareAccess.v1");

    internal bool IsGranted(HttpContext http, string clipId, string token)
    {
        if (!http.Request.Cookies.TryGetValue(COOKIE_NAME, out var grant) || string.IsNullOrEmpty(grant))
        {
            return false;
        }
        try
        {
            return ForShare(http, clipId, token).Unprotect(grant) == "1";
        }
        catch (Exception error) when (error is CryptographicException or FormatException)
        {
            return false;
        }
    }

    internal void Grant(HttpContext http, string clipId, string token)
    {
        var pathBase = http.Request.PathBase.ToUriComponent();
        var clip = Uri.EscapeDataString(clipId);
        var share = Uri.EscapeDataString(token);
        var grant = ForShare(http, clipId, token).Protect("1", VALIDITY);
        http.Response.Cookies.Append(COOKIE_NAME, grant, new()
        {
            HttpOnly = true,
            Secure = http.Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            IsEssential = true,
            Path = $"{pathBase}/api/clips/{clip}/share/{share}",
            MaxAge = VALIDITY
        });
    }

    private ITimeLimitedDataProtector ForShare(HttpContext http, string clipId, string token)
    {
        var userId = http.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var version = UserPrincipalFactory.GetSessionVersion(http.User)!.Value.ToString(CultureInfo.InvariantCulture);
        return protector.CreateProtector(userId).CreateProtector(version).CreateProtector(clipId).CreateProtector(token)
            .ToTimeLimitedDataProtector();
    }
}
