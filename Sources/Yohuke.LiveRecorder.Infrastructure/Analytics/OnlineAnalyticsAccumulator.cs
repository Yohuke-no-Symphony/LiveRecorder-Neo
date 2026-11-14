using System.Text.Json;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Infrastructure.Analytics;

/// <summary>按原始档案独立累积在线采样，以六十秒上限和明确断连限制有效观测。</summary>
public sealed class OnlineAnalyticsAccumulator
{
    public const double VALIDITY_MS = 60000;
    private readonly List<OnlineAnalyticsObservation> observations = [];

    /// <summary>只接受在线人数协议字段和明确连接中断，重复数值同样更新有效期限。</summary>
    public void Add(RecordedEventEnvelope item)
    {
        if (!double.IsFinite(item.OffsetMs) || item.OffsetMs < 0)
        {
            return;
        }
        var command = item.Command.Split(':', 2)[0];
        if (command == "RECORDER_DISCONNECTED" || item.TextCode == "RECORDING_EVENT_CONNECTION_LOST")
        {
            observations.Add(new(item.OffsetMs, item.Sequence, null));
        }
        else if (item.Kind == "Raw" && command == "ONLINE_RANK_COUNT" && ReadCount(item) is { } count)
        {
            observations.Add(new(item.OffsetMs, item.Sequence, count));
        }
    }

    /// <summary>在原始档案范围内恢复起点基线，裁切后投影到录像绝对时间轴。</summary>
    public OnlineAnalyticsReport Build(double fromMs, double toMs, double offsetMs = 0)
    {
        if (!double.IsFinite(fromMs) || !double.IsFinite(toMs) || !double.IsFinite(offsetMs) || fromMs < 0 || toMs < fromMs)
        {
            throw new ArgumentOutOfRangeException(nameof(fromMs));
        }
        var ordered = observations.OrderBy(item => item.OffsetMs).ThenBy(item => item.Sequence).ToArray();
        var intervals = new List<OnlineAnalyticsInterval>();
        for (var index = 0; index < ordered.Length; index++)
        {
            var sample = ordered[index];
            if (sample.Count is not { } count || sample.OffsetMs >= toMs)
            {
                continue;
            }
            var next = index + 1 < ordered.Length ? ordered[index + 1].OffsetMs : toMs;
            var from = Math.Max(fromMs, sample.OffsetMs);
            var to = Math.Min(toMs, Math.Min(next, sample.OffsetMs + VALIDITY_MS));
            if (to <= from)
            {
                continue;
            }
            OnlineAnalyticsProjection.Append(intervals, from + offsetMs, to + offsetMs, count);
        }
        return OnlineAnalyticsProjection.Summarize(intervals, toMs - fromMs);
    }

    private static long? ReadCount(RecordedEventEnvelope item)
    {
        var json = item.RawJson.Length > 0 ? item.RawJson : item.Payload?.ToJsonString();
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) &&
                data.ValueKind == JsonValueKind.Object && data.TryGetProperty("online_count", out var value) &&
                value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var count) && count >= 0)
            {
                return count;
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }
}
