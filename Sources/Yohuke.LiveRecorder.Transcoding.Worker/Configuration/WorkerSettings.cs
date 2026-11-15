using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Configuration;

internal sealed record WorkerSettings
{
    public WorkerConnectionOptions Worker { get; set; } = new();
    public WorkerConfiguration Configuration { get; set; } = new();
    public int ConfigurationRevision { get; set; }
    public string? ConfigurationHash { get; set; }
    public string? BindingHash { get; set; }
}
