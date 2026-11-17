using System.Text.Json.Serialization;

namespace Yohuke.LiveRecorder.Application.Auth;

/// <summary>区分创建新实例与验证并恢复已有实例。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<InstallationMode>))]
public enum InstallationMode
{
    New,
    Restore
}
