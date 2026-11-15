using System.Globalization;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Execution;

internal sealed class WorkerEncodingProgressParser(Action<WorkerEncodingProgress> report)
{
    private double durationMs;
    private double? framesPerSecond;
    private double? speed;

    internal void Add(string line)
    {
        var separator = line.IndexOf('=');
        if (separator <= 0)
        {
            return;
        }
        var value = line[(separator + 1)..].Trim();
        switch (line[..separator])
        {
            case "out_time_us":
                if (Number(value) is { } microseconds)
                {
                    durationMs = microseconds / 1000;
                }
                break;
            case "fps":
                framesPerSecond = Number(value);
                break;
            case "speed":
                speed = Number(value.TrimEnd('x'));
                break;
            case "progress":
                report(new(durationMs, framesPerSecond, speed));
                break;
        }
    }

    private static double? Number(string value) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
        && double.IsFinite(result) && result >= 0 ? result : null;
}
