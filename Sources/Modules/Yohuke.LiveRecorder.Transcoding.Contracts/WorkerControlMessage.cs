using System.Text.Json;

namespace Yohuke.LiveRecorder.Transcoding.Contracts;

/// <summary>带版本和请求关联的有界控制消息。</summary>
public sealed record WorkerControlMessage
{
    public int Version { get; set; } = 1;
    public string Type { get; set; } = "";
    public string RequestId { get; set; } = "";
    public JsonElement Payload { get; set; }
}
