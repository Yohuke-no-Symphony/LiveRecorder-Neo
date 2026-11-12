using Yohuke.DanmuCore.Apis.Models.Bilibili;
using Yohuke.DanmuCore.Services;

namespace Yohuke.LiveRecorder.Infrastructure.Bilibili;

/// <summary>结合平台稳定标识与 Core 跨来源出现次数对账，保留合法重复发言。</summary>
public sealed class BilibiliDanmuReconciliation
{
    private const int CAPACITY = 4096;
    private readonly object gate = new();
    private readonly DanmuHistoryReconciler reconciler = new();
    private readonly Dictionary<string, bool> seen = new(StringComparer.Ordinal);
    private readonly Queue<string> order = new();

    /// <summary>接收实时消息；可靠 ID 的重送不会再次投影。</summary>
    public bool AcceptLive(BilibiliDanmuMessage message)
    {
        lock (gate)
        {
            var key = Key(message);
            if (key is not null && seen.TryGetValue(key, out var liveSeen))
            {
                if (!liveSeen)
                {
                    reconciler.AcceptLive(message);
                    seen[key] = true;
                }
                return false;
            }
            var accepted = reconciler.AcceptLive(message);
            Remember(key, true);
            return accepted;
        }
    }

    /// <summary>按出现次数接收近期历史，ID 缺失时不使用内容哈希集合删除重复。</summary>
    public IReadOnlyList<BilibiliDanmuMessage> AcceptHistory(IEnumerable<BilibiliDanmuMessage> messages)
    {
        lock (gate)
        {
            var batchIds = new HashSet<string>(StringComparer.Ordinal);
            var batch = messages.Where(message => Key(message) is not { } key || batchIds.Add(key)).ToArray();
            var accepted = reconciler.AcceptHistory(batch).Where(message => Key(message) is not { } key || !seen.ContainsKey(key)).ToArray();
            var acceptedReferences = new HashSet<BilibiliDanmuMessage>(accepted, ReferenceEqualityComparer.Instance);
            foreach (var message in batch)
            {
                Remember(Key(message), !acceptedReferences.Contains(message));
            }
            return accepted;
        }
    }

    private void Remember(string? key, bool liveSeen)
    {
        if (key is null)
        {
            return;
        }
        if (seen.TryAdd(key, liveSeen))
        {
            order.Enqueue(key);
        }
        while (order.Count > CAPACITY)
        {
            seen.Remove(order.Dequeue());
        }
    }

    private static string? Key(BilibiliDanmuMessage message) => string.IsNullOrWhiteSpace(message.MessageId) ? null : message.RoomId + ":" + message.MessageId;
}
