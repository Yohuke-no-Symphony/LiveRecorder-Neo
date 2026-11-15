namespace Yohuke.LiveRecorder.Media;

internal sealed class TranscodePacketStatistics
{
    internal long Bytes { get; set; }
    internal double FirstPts { get; set; } = double.PositiveInfinity;
    internal double LastPts { get; set; } = double.NegativeInfinity;
    internal double Duration => Math.Max(0, LastPts - FirstPts);
    internal long Bitrate => Duration > 0 ? (long)Math.Ceiling(Bytes * 8d / Duration) : 0;
}
