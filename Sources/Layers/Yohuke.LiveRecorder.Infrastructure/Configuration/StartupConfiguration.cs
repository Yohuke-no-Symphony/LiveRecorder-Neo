using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yohuke.LiveRecorder.Infrastructure.Configuration;

/// <summary>唯一设置文件中的目录和全局参数，保留未识别的配置字段。</summary>
public sealed class StartupConfiguration
{
    [JsonRequired]
    [JsonPropertyName("DataDirectory")]
    public string DataDirectory { get; set; } = "Data";

    [JsonPropertyName("RecordsDirectory")]
    public string? RecordsDirectory { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalProperties { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
