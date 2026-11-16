using System.Text.Json.Serialization;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Domain;

/// <summary>主站登记的远端执行节点及其独立配置。</summary>
public sealed class TranscodeWorker : Entity
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "Transcoding";
    public bool Enabled { get; set; } = true;
    [JsonIgnore]
    public string SecretHash { get; set; } = "";
    public WorkerConfiguration Configuration { get; set; } = new();
    public int ConfigurationRevision { get; set; } = 1;
    public int AppliedConfigurationRevision { get; set; }
    public WorkerCapabilities Capabilities { get; set; } = new();
    public WorkerTelemetry Telemetry { get; set; } = new();
    public DateTime? LastSeenAtUtc { get; set; }
    public string? ConfigurationError { get; set; }
}
