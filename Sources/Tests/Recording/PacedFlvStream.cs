using System.Diagnostics;

namespace Yohuke.LiveRecorder.Recording.Tests;

internal sealed class PacedFlvStream : Stream
{
    private readonly byte[] data;
    private readonly IReadOnlyList<FlvReadChunk> chunks;
    private readonly ControlledRoom room;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private int chunkIndex;
    private int chunkOffset;
    private long position;
    private int disposed;

    internal PacedFlvStream(byte[] data, IReadOnlyList<FlvReadChunk> chunks, ControlledRoom room)
    {
        this.data = data;
        this.chunks = chunks;
        this.room = room;
        room.OpenStream();
    }

    public override bool CanRead => disposed == 0;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => chunks.Sum(x => x.Length);
    public override long Position { get => position; set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        await room.BeforeReadAsync(cancellationToken);
        if (buffer.Length == 0 || chunkIndex >= chunks.Count)
        {
            return 0;
        }
        var chunk = chunks[chunkIndex];
        var due = chunk.TimestampMs * room.PaceScale - clock.Elapsed.TotalMilliseconds;
        if (due > 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(due), cancellationToken);
        }
        var count = Math.Min(buffer.Length, chunk.Length - chunkOffset);
        data.AsMemory(chunk.Offset + chunkOffset, count).CopyTo(buffer);
        chunkOffset += count;
        position += count;
        room.Read(count);
        if (chunkOffset == chunk.Length)
        {
            chunkIndex++;
            chunkOffset = 0;
        }
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            room.CloseStream();
        }
        base.Dispose(disposing);
    }
}
