namespace Yohuke.LiveRecorder.Http.Tests;

internal sealed class RecordingLiveTestStream(byte[] bytes, TimeSpan readDelay) : Stream
{
    private int position;
    internal long ReadBytes => Volatile.Read(ref position);
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => ReadBytes; set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }
        await Task.Delay(readDelay, cancellationToken);
        if (position == bytes.Length)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        var count = Math.Min(Math.Min(buffer.Length, 1024), bytes.Length - position);
        bytes.AsMemory(position, count).CopyTo(buffer);
        Interlocked.Add(ref position, count);
        return count;
    }

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
