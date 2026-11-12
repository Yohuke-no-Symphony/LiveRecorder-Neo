using Newtonsoft.Json;
using Yohuke.DanmuCore.Apis.Models.Bilibili;

namespace Yohuke.LiveRecorder.Infrastructure.Bilibili;

internal sealed class BilibiliRedPocketRecovery(BilibiliCapturePolicy policy)
{
    private readonly object gate = new();
    private readonly Dictionary<long, RedPocketRecoveryState> states = [];
    internal bool CoverageIncomplete { get; private set; }

    internal BilibiliRedPocketMessage? Observe(BilibiliRedPocketMessage incoming, DateTimeOffset observedAt)
    {
        lock (gate)
        {
            var lotId = incoming.RedPocket.LotId;
            if (lotId <= 0)
            {
                return null;
            }
            if (!states.TryGetValue(lotId, out var state))
            {
                if (states.Count >= policy.MaxTrackedRedPockets)
                {
                    var oldest = states.Values.OrderByDescending(x => x.Message.RedPocket.HasResult).ThenBy(x => x.FirstSeenAtUtc).First();
                    CoverageIncomplete |= !oldest.Message.RedPocket.HasResult;
                    states.Remove(oldest.Message.RedPocket.LotId);
                }
                state = new(incoming, observedAt);
                states.Add(lotId, state);
            }
            var merged = Merge(state.Message, incoming);
            var data = merged.RedPocket;
            var end = DrawTime(data);
            if (!merged.IsDrawn && !data.HasResult)
            {
                var due = end is > 0 and <= 253402300799 ? DateTimeOffset.FromUnixTimeSeconds(end).AddSeconds(2) : state.FirstSeenAtUtc.AddMinutes(1);
                if (state.WinnersAttempts == 0 || end > 0 && end != DrawTime(state.Message.RedPocket))
                {
                    state.NextWinnersAtUtc = due > observedAt ? due : observedAt;
                }
            }
            else if (state.Message.IsDrawn != merged.IsDrawn)
            {
                state.NextWinnersAtUtc = observedAt;
            }
            state.Message = merged;
            var fingerprint = JsonConvert.SerializeObject(new { merged.IsDrawn, RedPocket = data with { CurrentTime = 0 } });
            if (fingerprint == state.Fingerprint)
            {
                return null;
            }
            state.Fingerprint = fingerprint;
            return merged;
        }
    }

    internal IReadOnlyList<RedPocketRecoveryRequest> TakeDue(DateTimeOffset now)
    {
        lock (gate)
        {
            foreach (var expired in states.Values.Where(x => now - x.FirstSeenAtUtc > policy.TrackedRedPocketLifetime).ToArray())
            {
                CoverageIncomplete |= !expired.Message.RedPocket.HasResult;
                states.Remove(expired.Message.RedPocket.LotId);
            }
            var requests = new List<RedPocketRecoveryRequest>();
            foreach (var state in states.Values.OrderBy(x => x.FirstSeenAtUtc))
            {
                if (requests.Count >= policy.MaxRedPocketRequestsPerCycle)
                {
                    break;
                }
                var needsInfo = !state.Message.RedPocket.HasDetails || state.Message.RedPocket.EndTime == 0 && !state.Message.RedPocket.HasResult;
                if (needsInfo && state.InfoAttempts >= policy.MaxRedPocketAttempts && state.NextInfoAtUtc <= now || !state.Message.RedPocket.HasResult && state.WinnersAttempts >= policy.MaxRedPocketAttempts && state.NextWinnersAtUtc <= now)
                {
                    CoverageIncomplete = true;
                }
                if (needsInfo && state.InfoAttempts < policy.MaxRedPocketAttempts && state.NextInfoAtUtc <= now)
                {
                    state.InfoAttempts++;
                    state.NextInfoAtUtc = now + Retry(state.InfoAttempts);
                    requests.Add(new(state.Message.RedPocket.LotId, false));
                }
                if (requests.Count < policy.MaxRedPocketRequestsPerCycle && !state.Message.RedPocket.HasResult && state.WinnersAttempts < policy.MaxRedPocketAttempts && state.NextWinnersAtUtc <= now)
                {
                    state.WinnersAttempts++;
                    state.NextWinnersAtUtc = now + Retry(state.WinnersAttempts);
                    requests.Add(new(state.Message.RedPocket.LotId, true));
                }
            }
            return requests;
        }
    }

    private TimeSpan Retry(int attempt) => TimeSpan.FromTicks(policy.RedPocketRetryInterval.Ticks * Math.Min(16, 1 << Math.Min(attempt - 1, 4)));

    private static long DrawTime(RedPocketData data) => data.EndTime > 0 ? data.EndTime : data.StartTime > 0 && data.Duration is > 0 ? data.StartTime + data.Duration.Value : 0;

    private static BilibiliRedPocketMessage Merge(BilibiliRedPocketMessage previous, BilibiliRedPocketMessage incoming)
    {
        var old = previous.RedPocket;
        var value = incoming.RedPocket;
        return incoming with
        {
            IsDrawn = previous.IsDrawn || incoming.IsDrawn,
            RedPocket = value with
            {
                Type = value.Type ?? old.Type,
                SenderUid = value.SenderUid > 0 ? value.SenderUid : old.SenderUid,
                SenderName = Text(value.SenderName, old.SenderName),
                SenderAvatarUrl = Text(value.SenderAvatarUrl, old.SenderAvatarUrl),
                IsMystery = value.IsMystery ?? old.IsMystery,
                StartTime = value.StartTime > 0 ? value.StartTime : old.StartTime,
                EndTime = value.EndTime > 0 ? value.EndTime : old.EndTime,
                Duration = value.Duration ?? old.Duration,
                LotStatus = value.LotStatus ?? old.LotStatus,
                UserStatus = value.UserStatus ?? old.UserStatus,
                JoinRequirement = value.JoinRequirement ?? old.JoinRequirement,
                Danmu = Text(value.Danmu, old.Danmu),
                H5Url = Text(value.H5Url, old.H5Url),
                Price = value.Price ?? old.Price,
                TotalPrice = value.TotalPrice ?? old.TotalPrice,
                TotalBattery = value.TotalBattery ?? old.TotalBattery,
                TotalAwardCount = value.TotalAwardCount ?? old.TotalAwardCount,
                HasDetails = value.HasDetails || old.HasDetails,
                HasResult = value.HasResult || old.HasResult,
                Awards = value.Awards.Count > 0 ? value.Awards : old.Awards,
                Winners = MergeWinners(old.Winners, value.Winners)
            }
        };
    }

    private static IReadOnlyList<RedPocketWinner> MergeWinners(IReadOnlyList<RedPocketWinner> old, IReadOnlyList<RedPocketWinner> current)
    {
        var result = old.ToList();
        var known = old.GroupBy(JsonConvert.SerializeObject).ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var winner in current)
        {
            var key = JsonConvert.SerializeObject(winner);
            counts[key] = counts.GetValueOrDefault(key) + 1;
            if (counts[key] > known.GetValueOrDefault(key))
            {
                result.Add(winner);
            }
        }
        return result;
    }

    private static string Text(string current, string previous) => string.IsNullOrWhiteSpace(current) ? previous : current;
}
