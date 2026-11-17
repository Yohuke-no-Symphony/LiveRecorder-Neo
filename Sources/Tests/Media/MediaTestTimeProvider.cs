namespace Yohuke.LiveRecorder.Media.Tests;

internal sealed class MediaTestTimeProvider(DateTimeOffset now) : TimeProvider
{
    internal DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}
