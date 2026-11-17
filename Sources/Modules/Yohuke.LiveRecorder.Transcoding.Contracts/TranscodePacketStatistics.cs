using System.Globalization;

namespace Yohuke.LiveRecorder.Transcoding.Contracts;

/// <summary>按有效媒体包的字节与显示时间范围计算平均码率。</summary>
public sealed class TranscodePacketStatistics
{
    public long Bytes { get; private set; }
    public double FirstPts { get; private set; } = double.PositiveInfinity;
    public double LastPts { get; private set; } = double.NegativeInfinity;
    public double Duration => Math.Max(0, LastPts - FirstPts);
    public long Bitrate => Duration > 0 ? (long)Math.Ceiling(Bytes * 8d / Duration) : 0;

    /// <summary>将 ffprobe compact 格式的一行媒体包信息计入对应流。</summary>
    public static void Parse(string line, IReadOnlyDictionary<int, TranscodePacketStatistics> streams)
    {
        var index = -1;
        var size = 0L;
        var pts = double.NaN;
        var duration = 0d;
        foreach (var field in line.Split('|'))
        {
            var separator = field.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }
            var name = field.AsSpan(0, separator);
            var value = field.AsSpan(separator + 1);
            if (name.SequenceEqual("stream_index"))
            {
                int.TryParse(value, CultureInfo.InvariantCulture, out index);
            }
            else if (name.SequenceEqual("size"))
            {
                long.TryParse(value, CultureInfo.InvariantCulture, out size);
            }
            else if (name.SequenceEqual("pts_time"))
            {
                if (!double.TryParse(value, CultureInfo.InvariantCulture, out pts))
                {
                    pts = double.NaN;
                }
            }
            else if (name.SequenceEqual("duration_time"))
            {
                double.TryParse(value, CultureInfo.InvariantCulture, out duration);
            }
        }
        if (size <= 0 || !double.IsFinite(pts) || !double.IsFinite(duration)
            || !streams.TryGetValue(index, out var statistics))
        {
            return;
        }
        statistics.Bytes += size;
        statistics.FirstPts = Math.Min(statistics.FirstPts, pts);
        statistics.LastPts = Math.Max(statistics.LastPts, pts + Math.Max(0, duration));
    }
}
