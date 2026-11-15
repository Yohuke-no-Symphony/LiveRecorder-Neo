using System.Globalization;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Encoding;

internal sealed class PacketBitrate
{
    private long bytes;
    private double? first;
    private double? last;

    internal void Add(long size, double? time, double duration)
    {
        bytes = checked(bytes + size);
        if (time is { } timestamp && double.IsFinite(timestamp))
        {
            first = Math.Min(first ?? timestamp, timestamp);
            last = Math.Max(last ?? timestamp, timestamp + Math.Max(0, duration));
        }
    }

    internal long Calculate(double fallbackDurationMs)
    {
        var seconds = first is { } start && last is { } end && end > start ? end - start : fallbackDurationMs / 1000;
        return seconds > 0 && bytes > 0 ? (long)(bytes * 8 / seconds) : 0;
    }

    internal static void Parse(string line, Dictionary<int, PacketBitrate> streams)
    {
        var index = -1;
        var size = 0L;
        double? time = null;
        var duration = 0d;
        foreach (var part in line.Split('|'))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2)
            {
                continue;
            }
            if (pair[0] == "stream_index")
            {
                _ = int.TryParse(pair[1], CultureInfo.InvariantCulture, out index);
            }
            else if (pair[0] == "size")
            {
                _ = long.TryParse(pair[1], CultureInfo.InvariantCulture, out size);
            }
            else if (pair[0] == "pts_time" && double.TryParse(pair[1], CultureInfo.InvariantCulture, out var timestamp))
            {
                time = timestamp;
            }
            else if (pair[0] == "duration_time")
            {
                _ = double.TryParse(pair[1], CultureInfo.InvariantCulture, out duration);
            }
        }
        if (size > 0 && streams.TryGetValue(index, out var accumulator))
        {
            accumulator.Add(size, time, duration);
        }
    }
}
