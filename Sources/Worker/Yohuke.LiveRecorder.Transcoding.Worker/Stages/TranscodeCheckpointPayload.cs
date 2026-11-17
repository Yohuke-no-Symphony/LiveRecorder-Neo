using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Worker.Common.Storage;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Stages;

internal sealed class TranscodeCheckpointPayload
{
    public List<WorkerArtifact> SourceArtifacts { get; set; } = [];
    public TranscodeMediaInfo? Source { get; set; }
    public TranscodeMediaInfo? ExpectedMedia { get; set; }
    public string? OutputDirectory { get; set; }
    public List<WorkerArtifact> EncodedArtifacts { get; set; } = [];
    public string? Encoder { get; set; }
    public long VideoBitrate { get; set; }
    public long AudioBitrate { get; set; }
    public int EncodingAttempts { get; set; }
    public int UploadedAttempt { get; set; }
    public bool PublicationPending { get; set; }
    public TranscodeOutputManifest? Manifest { get; set; }
}
