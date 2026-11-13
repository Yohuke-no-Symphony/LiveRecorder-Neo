namespace Yohuke.LiveRecorder.Tests;

internal sealed class ArtworkTestTimeProvider(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset current = now;

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => current;

    internal void Advance(TimeSpan amount)
    {
        current += amount;
    }
}
