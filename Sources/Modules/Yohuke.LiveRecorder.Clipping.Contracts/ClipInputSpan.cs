namespace Yohuke.LiveRecorder.Clipping.Contracts;

/// <summary>Maps a bounded source or a real recording gap onto the session timeline.</summary>
public sealed record ClipInputSpan
{
    public string Id { get; init; } = "";
    public bool IsGap { get; init; }
    public double SessionStartMs { get; init; }
    public double SourceStartMs { get; init; }
    public double DurationMs { get; init; }
    public string SourceETag { get; init; } = "";
    public string SourceVersion { get; init; } = "";
    public string SourceFileName { get; init; } = "";
    public string RangeSha256 { get; init; } = "";
    public ClipSourceRange? Range { get; init; }
}
