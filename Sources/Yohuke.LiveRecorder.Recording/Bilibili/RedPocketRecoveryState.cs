using Yohuke.DanmuCore.Apis.Models.Bilibili;

namespace Yohuke.LiveRecorder.Infrastructure.Bilibili;

internal sealed class RedPocketRecoveryState(BilibiliRedPocketMessage message, DateTimeOffset observedAt)
{
    internal BilibiliRedPocketMessage Message { get; set; } = message;
    internal DateTimeOffset FirstSeenAtUtc { get; } = observedAt;
    internal DateTimeOffset NextInfoAtUtc { get; set; } = observedAt;
    internal DateTimeOffset NextWinnersAtUtc { get; set; } = observedAt;
    internal int InfoAttempts { get; set; }
    internal int WinnersAttempts { get; set; }
    internal string Fingerprint { get; set; } = "";
}
