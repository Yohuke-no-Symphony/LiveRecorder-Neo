using Yohuke.DanmuCore.Apis.Models.Bilibili;

namespace Yohuke.LiveRecorder.Infrastructure.Bilibili;

internal sealed record BilibiliCaptureItem
{
    internal BilibiliRawBusinessEvent? Raw { get; init; }
    internal IReadOnlyList<BilibiliDanmuMessage>? History { get; init; }
    internal BilibiliRedPocketMessage? RedPocket { get; init; }
    internal DateTimeOffset ObservedAtUtc { get; init; }
    internal string Command { get; init; } = "";
}
