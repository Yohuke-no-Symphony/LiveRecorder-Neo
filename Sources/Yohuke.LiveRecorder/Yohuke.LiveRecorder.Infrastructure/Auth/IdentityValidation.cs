using Yohuke.LiveRecorder.Application.Localization;
using System.Net.Mail;
using Yohuke.LiveRecorder.Application;

namespace Yohuke.LiveRecorder.Infrastructure.Auth;

/// <summary>统一身份输入的校验与邮箱规范化。</summary>
public static class IdentityValidation
{
    /// <summary>验证完整邮箱地址并返回统一查询键。</summary>
    public static string NormalizeEmail(string email)
    {
        var value = email?.Trim() ?? "";
        if (value.Length is < 3 or > 254 || !MailAddress.TryCreate(value, out var parsed) || parsed.Address != value || value.Contains('\r') || value.Contains('\n'))
        {
            throw new InfrastructureException(MessageCode.AUTH_EMAIL_INVALID);
        }
        return value.ToLowerInvariant();
    }

    /// <summary>验证新设密码长度。</summary>
    public static void ValidatePassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length is < 12 or > 256)
        {
            throw new InfrastructureException(MessageCode.AUTH_PASSWORD_LENGTH_INVALID);
        }
    }

    /// <summary>验证并规范化用户显示名称。</summary>
    public static string NormalizeDisplayName(string displayName)
    {
        var value = displayName?.Trim() ?? "";
        if (value.Length is < 1 or > 80 || value.Any(char.IsControl))
        {
            throw new InfrastructureException(MessageCode.AUTH_DISPLAY_NAME_LENGTH_INVALID);
        }
        return value;
    }
}
