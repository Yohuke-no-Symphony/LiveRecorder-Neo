using System.Globalization;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Encoding;

internal sealed class HlsPacketTimeline(IReadOnlyList<double> boundaries, double frameRate)
{
    private readonly double tolerance = 1.5 / frameRate;
    private double? origin;
    private double? lastDts;
    private int nextBoundary;
    private string? error;

    internal void Add(string line)
    {
        double? pts = null;
        double? dts = null;
        var keyframe = false;
        foreach (var part in line.Split('|'))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2)
            {
                continue;
            }
            if (pair[0] == "pts_time" && double.TryParse(pair[1], CultureInfo.InvariantCulture, out var parsedPts))
            {
                pts = parsedPts;
            }
            else if (pair[0] == "dts_time" && double.TryParse(pair[1], CultureInfo.InvariantCulture, out var parsedDts))
            {
                dts = parsedDts;
            }
            else if (pair[0] == "flags")
            {
                keyframe = pair[1].Contains('K');
            }
        }
        if (pts is not { } timestamp || !double.IsFinite(timestamp))
        {
            error ??= "A transcoded packet is missing a finite video timestamp";
            return;
        }
        if (dts is { } decodeTime)
        {
            if (!double.IsFinite(decodeTime) || lastDts is { } previous && decodeTime < previous - 0.000001)
            {
                error ??= "Decoding timestamps in transcoded segments moved backward";
            }
            lastDts = decodeTime;
        }
        if (origin is null)
        {
            origin = timestamp;
            if (!keyframe)
            {
                error ??= "The transcoded video does not start with a keyframe";
            }
        }
        if (!keyframe)
        {
            return;
        }
        var position = timestamp - origin.Value;
        while (nextBoundary < boundaries.Count && position >= boundaries[nextBoundary] - tolerance)
        {
            if (Math.Abs(position - boundaries[nextBoundary]) > tolerance)
            {
                error ??= "A transcoded segment boundary is missing an independent keyframe";
            }
            nextBoundary++;
        }
    }

    internal void Complete()
    {
        if (error is not null || origin is null || nextBoundary != boundaries.Count)
        {
            throw new InvalidDataException(error ?? "The transcoded video does not cover all segment boundaries");
        }
    }
}
