using Yohuke.LiveRecorder.Application.Media;
using Yohuke.LiveRecorder.Clipping.Contracts;
using Yohuke.LiveRecorder.Worker.Common.Storage;

namespace Yohuke.LiveRecorder.Clipping.Worker.Stages;

internal sealed class ClipCheckpointPayload
{
    public List<WorkerArtifact> RawArtifacts { get; set; } = [];
    public List<WorkerArtifact> PreparedArtifacts { get; set; } = [];
    public List<WorkerArtifact> OutputArtifacts { get; set; } = [];
    public string? OutputPath { get; set; }
    public MediaExportResult? ExportResult { get; set; }
    public ClipOutputManifest? Manifest { get; set; }
    public int UploadedAttempt { get; set; }
    public bool PublicationPending { get; set; }
}
