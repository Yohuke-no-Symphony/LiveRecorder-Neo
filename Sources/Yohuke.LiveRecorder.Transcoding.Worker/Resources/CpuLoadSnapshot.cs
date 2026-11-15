namespace Yohuke.LiveRecorder.Transcoding.Worker.Resources;

internal sealed record CpuLoadSnapshot(double? AveragePercent, double EffectiveProcessors, string? UnavailableReason)
{
    internal static CpuLoadSnapshot Unknown(string reason) => new(null, 0, reason);
}
