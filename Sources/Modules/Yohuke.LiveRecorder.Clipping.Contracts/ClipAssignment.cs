namespace Yohuke.LiveRecorder.Clipping.Contracts;

/// <summary>Describes a fixed clipping attempt and its bounded media inputs.</summary>
public sealed record ClipAssignment
{
    public string JobId { get; init; } = "";
    public int Attempt { get; init; }
    public string LeaseToken { get; init; } = "";
    public int LeaseSeconds { get; init; } = 90;
    public string Mode { get; init; } = "Exact";
    public double StartMs { get; init; }
    public double EndMs { get; init; }
    public double ActualStartMs { get; init; }
    public double ActualEndMs { get; init; }
    public List<ClipInputSpan> Inputs { get; init; } = [];
}
