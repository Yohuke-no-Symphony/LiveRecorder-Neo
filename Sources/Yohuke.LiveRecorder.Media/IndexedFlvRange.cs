using System.Buffers;
using System.Buffers.Binary;

namespace Yohuke.LiveRecorder.Media;

internal static class IndexedFlvRange
{
    internal static async Task CopyAsync(string path, FlvIndexSnapshot index, FlvIndexEntry first, long endOffset, Stream output, CancellationToken cancellationToken)
    {
        var video = index.Entries.LastOrDefault(entry => entry.Kind == "VideoHeader" && entry.Offset < first.Offset);
        var audio = index.Entries.LastOrDefault(entry => entry.Kind == "AudioHeader" && entry.Offset < first.Offset);
        if (video?.HeaderData is null)
        {
            throw new InvalidDataException("No indexed AVC/HEVC decoder configuration is available for this window.");
        }

        await CopyAsync(path, first, endOffset, first.TimestampMs, video.HeaderData, audio?.HeaderData, output, cancellationToken);
    }

    internal static async Task CopyAsync(string path, FlvIndexEntry first, long endOffset, double timestampOriginMs, byte[] videoHeader, byte[]? audioHeader,
        Stream output, CancellationToken cancellationToken)
    {
        await output.WriteAsync(new byte[] { 70, 76, 86, 1, (byte)(audioHeader is null ? 1 : 5), 0, 0, 0, 9, 0, 0, 0, 0 }, cancellationToken);
        await WriteHeaderAsync(output, 9, videoHeader, cancellationToken);
        if (audioHeader is { } audioData)
        {
            await WriteHeaderAsync(output, 8, audioData, cancellationToken);
        }

        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, FileOptions.Asynchronous);
        source.Position = first.Offset;
        var header = new byte[11];
        var rented = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            while (source.Position + 15 <= endOffset)
            {
                await source.ReadExactlyAsync(header, cancellationToken);
                var size = (header[1] << 16) | (header[2] << 8) | header[3];
                if (source.Position + size + 4 > endOffset)
                {
                    throw new InvalidDataException("The committed window ends inside an FLV tag.");
                }
                var timestamp = (header[7] << 24) | (header[4] << 16) | (header[5] << 8) | header[6];
                var relative = Math.Max(0, timestamp - (int)timestampOriginMs);
                header[4] = (byte)(relative >> 16);
                header[5] = (byte)(relative >> 8);
                header[6] = (byte)relative;
                header[7] = (byte)(relative >> 24);
                await output.WriteAsync(header, cancellationToken);
                var remaining = size + 4;
                while (remaining > 0)
                {
                    var count = Math.Min(rented.Length, remaining);
                    await source.ReadExactlyAsync(rented.AsMemory(0, count), cancellationToken);
                    await output.WriteAsync(rented.AsMemory(0, count), cancellationToken);
                    remaining -= count;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static async Task WriteHeaderAsync(Stream output, byte type, byte[] payload, CancellationToken cancellationToken)
    {
        var header = new byte[11];
        header[0] = type;
        header[1] = (byte)(payload.Length >> 16);
        header[2] = (byte)(payload.Length >> 8);
        header[3] = (byte)payload.Length;
        await output.WriteAsync(header, cancellationToken);
        await output.WriteAsync(payload, cancellationToken);
        var previousSize = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(previousSize, 11 + payload.Length);
        await output.WriteAsync(previousSize, cancellationToken);
    }
}
