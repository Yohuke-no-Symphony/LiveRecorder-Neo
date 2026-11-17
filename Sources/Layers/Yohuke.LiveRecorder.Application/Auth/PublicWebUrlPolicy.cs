using System.Diagnostics.CodeAnalysis;
using Yohuke.LiveRecorder.Application.Localization;

namespace Yohuke.LiveRecorder.Application.Auth;

/// <summary>验证管理员配置的站点根地址，邮件链接不信任请求来源。</summary>
public static class PublicWebUrlPolicy
{
    /// <summary>规范化可选站点地址，拒绝非 HTTPS、非根路径及携带用户信息的地址。</summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }
        if (!TryGetBaseUri(value, out var uri))
        {
            throw new InfrastructureException(MessageCode.AUTH_PASSWORD_RESET_PUBLIC_URL_INVALID);
        }
        return uri.GetLeftPart(UriPartial.Authority);
    }

    /// <summary>读取可信站点根地址，仅允许 HTTPS 或本机 HTTP。</summary>
    public static bool TryGetBaseUri(string? value, [NotNullWhen(true)] out Uri? uri)
    {
        uri = null;
        var candidate = value?.Trim();
        if (string.IsNullOrEmpty(candidate) || candidate.Contains('\\') || candidate.Any(char.IsControl)
            || !Uri.TryCreate(candidate, UriKind.Absolute, out var parsed)
            || parsed.Scheme != Uri.UriSchemeHttps && !(parsed.Scheme == Uri.UriSchemeHttp && parsed.IsLoopback)
            || !string.IsNullOrEmpty(parsed.UserInfo) || !string.IsNullOrEmpty(parsed.Query)
            || !string.IsNullOrEmpty(parsed.Fragment) || parsed.AbsolutePath != "/"
            || parsed.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6))
        {
            return false;
        }
        uri = new Uri(parsed.GetLeftPart(UriPartial.Authority) + "/", UriKind.Absolute);
        return true;
    }
}
