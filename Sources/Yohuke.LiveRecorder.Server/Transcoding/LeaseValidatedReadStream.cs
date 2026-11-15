using System.Diagnostics;

namespace Yohuke.LiveRecorder.Server.Transcoding;

internal sealed class LeaseValidatedReadStream(Stream source, long length, Func<CancellationToken, Task> validate) : Stream
{
    private long lastValidated;

    public override bool CanRead => source.CanRead;
    public override bool CanSeek => source.CanSeek;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position
    {
        get => source.Position;
        set
        {
            if (value < 0 || value > length)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            source.Position = value;
        }
    }

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var now = Stopwatch.GetTimestamp();
        if (lastValidated == 0 || Stopwatch.GetElapsedTime(lastValidated, now) >= TimeSpan.FromSeconds(2))
        {
            await validate(cancellationToken);
            lastValidated = now;
        }
        var remaining = length - source.Position;
        return remaining <= 0 ? 0 : await source.ReadAsync(buffer[..(int)Math.Min(buffer.Length, remaining)], cancellationToken);
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(Position + offset),
            SeekOrigin.End => checked(length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        return Position;
    }

    /// <inheritdoc />
    public override void Flush() => source.Flush();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            source.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        await source.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
