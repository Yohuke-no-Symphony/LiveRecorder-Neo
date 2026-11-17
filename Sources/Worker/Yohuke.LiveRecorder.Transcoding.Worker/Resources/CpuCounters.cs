namespace Yohuke.LiveRecorder.Transcoding.Worker.Resources;

internal sealed record CpuCounters(double Busy, double Total, double EffectiveProcessors,
    double? QuotaUsedSeconds = null, string ResourceIdentity = "host");
