using System.Text.Json;
using System.Text;
using System.Security.Cryptography;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Files;

namespace Yohuke.LiveRecorder.Infrastructure.AI;

/// <summary>统一管理员设置中的 AI key 加密、保留、删除与脱敏。</summary>
public static class AiSettingsSecrets
{
    /// <summary>仅对受保护的密钥计算身份，用于检测作业执行期间的凭据更换。</summary>
    public static string Fingerprint(AiSettings settings) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(settings.ApiKey ?? "")));

    /// <summary>空输入保留旧 key，明确清除优先，返回值不携带操作标记。</summary>
    public static AiSettings Protect(AiSettings incoming, AiSettings previous, ISecretProtector protector)
    {
        var result = Clone(incoming);
        result.ApiKey = incoming.ClearApiKey ? "" : string.IsNullOrWhiteSpace(incoming.ApiKey) ? previous.ApiKey : protector.Protect(incoming.ApiKey.Trim());
        result.HasApiKey = false;
        result.ClearApiKey = false;
        return result;
    }

    /// <summary>仅向浏览器暴露是否已设置 key。</summary>
    public static AiSettings Redact(AiSettings settings)
    {
        var result = Clone(settings);
        result.HasApiKey = !string.IsNullOrEmpty(settings.ApiKey);
        result.ApiKey = "";
        result.ClearApiKey = false;
        return result;
    }

    private static AiSettings Clone(AiSettings settings) => JsonSerializer.Deserialize<AiSettings>(JsonSerializer.Serialize(settings, JsonFiles.Options), JsonFiles.Options)!;
}
