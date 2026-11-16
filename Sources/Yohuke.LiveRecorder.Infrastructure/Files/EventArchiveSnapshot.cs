namespace Yohuke.LiveRecorder.Infrastructure.Files;

internal sealed class EventArchiveSnapshot(FileStream stream, IReadOnlyList<EventIndexBlock> blocks, long committedLength) : IAsyncDisposable
{
    internal FileStream Stream { get; } = stream;
    internal IReadOnlyList<EventIndexBlock> Blocks { get; } = blocks;
    internal long CommittedLength { get; } = committedLength;

    /// <inheritdoc />
    public ValueTask DisposeAsync() => Stream.DisposeAsync();
}
