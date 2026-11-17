namespace Yohuke.LiveRecorder.Transcoding.Worker.Resources;

internal sealed record CpuObservation(TimeSpan Time, CpuCounters Counters);
