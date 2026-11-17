using System.Text.Json;
using Yohuke.LiveRecorder.Application.Media;

namespace Yohuke.LiveRecorder.Media;

internal sealed class FlvOutputState
{
    public required string Id { get; init; }
    public required string RelativePath { get; init; }
    public required string IndexRelativePath { get; init; }
    public required string Path { get; init; }
    public required string IndexPath { get; init; }
    public required FileStream Stream { get; init; }
    public required StreamWriter IndexWriter { get; init; }
    public double StartOffsetMs { get; init; }
    public double DurationMs { get; set; }
    public double EstimatedEndMs { get; set; }
    public double? ClosingDurationMs { get; set; }
    public double? NextPresentationStartMs { get; set; }
    public double? FirstVideoPresentationMs { get; set; }
    public double? FirstAudioPresentationMs { get; set; }
    public double PresentationStartMs => Math.Min(FirstVideoPresentationMs ?? FirstAudioPresentationMs ?? 0,
        FirstAudioPresentationMs ?? FirstVideoPresentationMs ?? 0);
    public double MetadataDurationMs => Math.Max(0, DurationMs + (NextPresentationStartMs ?? 0) - PresentationStartMs);
    public bool EndFinalized { get; set; }
    public double LastAudioTimestampMs { get; set; } = -1;
    public double AudioFrameDurationMs { get; set; } = 1024d / 48;
    public DateTime? LastMediaReceivedAtUtc { get; set; }
    public double LastVideoTimestampMs { get; set; } = -1;
    public double FrameDurationMs { get; set; } = 33.333333;
    public long CommittedBytes { get; set; }
    public long IndexCommittedBytes { get; set; }
    public long CommitSequence { get; set; }
    public MediaRecordingCommitSnapshot? CommitSnapshot { get; set; }
    public string Codec { get; set; } = "avc";

    internal Task WriteIndexAsync(FlvIndexEntry entry)
    {
        return IndexWriter.WriteLineAsync(JsonSerializer.Serialize(entry, MediaJson.Options));
    }
}
