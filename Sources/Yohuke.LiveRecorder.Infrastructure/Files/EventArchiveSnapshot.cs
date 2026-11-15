namespace Yohuke.LiveRecorder.Infrastructure.Files;

internal sealed class EventArchiveSnapshot(FileStream stream, IReadOnlyList<EventIndexBlock> blocks) : IAsyncDisposable
{
    internal FileStream Stream { get; } = stream;
    internal IReadOnlyList<EventIndexBlock> Blocks { get; } = blocks;

    /// <inheritdoc />
    public ValueTask DisposeAsync() => Stream.DisposeAsync();
}
