using System.Buffers;
using System.Security.Cryptography;

namespace Yohuke.LiveRecorder.Infrastructure.Clipping;

internal static class ClipRangeContentHasher
{
    internal static async Task<string> HashAsync(string path, long startOffset, long endOffset, CancellationToken cancellationToken)
    {
        if (startOffset < 0 || endOffset <= startOffset)
        {
            throw new InvalidDataException("Invalid committed clipping range.");
        }
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        source.Position = startOffset;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        var remaining = endOffset - startOffset;
        try
        {
            while (remaining > 0)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
                if (read == 0)
                {
                    throw new EndOfStreamException("The committed clipping range was truncated.");
                }
                hash.AppendData(buffer.AsSpan(0, read));
                remaining -= read;
            }
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
