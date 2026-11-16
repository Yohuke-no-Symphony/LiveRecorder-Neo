namespace Yohuke.LiveRecorder.Clipping.Contracts;

/// <summary>Identifies an uploaded MP4 and its actual session bounds for server-side verification.</summary>
public sealed record ClipOutputManifest
{
    public long Bytes { get; init; }
    public string Sha256 { get; init; } = "";
    public double ActualStartMs { get; init; }
    public double ActualEndMs { get; init; }
}
