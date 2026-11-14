namespace Yohuke.LiveRecorder.Server.Bandwidth;

internal sealed class BandwidthReadStream(Stream source, IMediaBandwidthScheduler scheduler, string userId, MediaTrafficKind kind) : Stream
{
    private MediaBandwidthTransfer? transfer;
    private int disposed;

    public override bool CanRead => Volatile.Read(ref disposed) == 0 && source.CanRead;
    public override bool CanSeek => Volatile.Read(ref disposed) == 0 && source.CanSeek;
    public override bool CanWrite => false;
    public override long Length => source.Length;

    public override long Position
    {
        get => source.Position;
        set => source.Position = value;
    }

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (buffer.IsEmpty)
        {
            return 0;
        }
        var remainingBytes = source.Length - source.Position;
        if (remainingBytes <= 0)
        {
            return 0;
        }
        var maximumBytes = (int)Math.Min(buffer.Length, remainingBytes);
        var currentTransfer = transfer;
        if (currentTransfer is null)
        {
            currentTransfer = await scheduler.OpenTransferAsync(userId, kind, cancellationToken);
            transfer = currentTransfer;
            if (Volatile.Read(ref disposed) != 0)
            {
                Interlocked.Exchange(ref transfer, null)?.Dispose();
                throw new ObjectDisposedException(nameof(BandwidthReadStream));
            }
        }
        using var permit = await currentTransfer.AcquireAsync(maximumBytes, cancellationToken);
        var count = await source.ReadAsync(buffer[..permit.Bytes], cancellationToken);
        permit.Commit(count);
        return count;
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("媒体带宽流仅支持异步读取。");

    /// <inheritdoc />
    public override int Read(Span<byte> buffer) => throw new NotSupportedException("媒体带宽流仅支持异步读取。");

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => source.Seek(offset, origin);

    /// <inheritdoc />
    public override void Flush() => source.Flush();

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken) => source.FlushAsync(cancellationToken);

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException("媒体带宽流为只读流。");

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("媒体带宽流为只读流。");

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref disposed, 1) == 0)
        {
            try
            {
                Interlocked.Exchange(ref transfer, null)?.Dispose();
            }
            finally
            {
                source.Dispose();
            }
        }
        base.Dispose(disposing);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            try
            {
                var activeTransfer = Interlocked.Exchange(ref transfer, null);
                if (activeTransfer is not null)
                {
                    await activeTransfer.DisposeAsync();
                }
            }
            finally
            {
                await source.DisposeAsync();
            }
        }
        GC.SuppressFinalize(this);
    }
}
