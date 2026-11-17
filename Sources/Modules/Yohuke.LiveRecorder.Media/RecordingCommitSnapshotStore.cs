using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Yohuke.LiveRecorder.Application.Media;

namespace Yohuke.LiveRecorder.Media;

internal sealed class RecordingCommitSnapshotStore(string directory)
{
    private const int FLV_HEADER_LENGTH = 13;
    private const int TAG_HEADER_LENGTH = 11;
    private const int PREVIOUS_TAG_SIZE_LENGTH = 4;

    internal MediaRecordingCommitSnapshot Write(FlvOutputState state, bool isClosed)
    {
        var prefix = ReadPrefix(state.Stream, state.CommittedBytes);
        var hash = Convert.ToHexString(SHA256.HashData(prefix)).ToLowerInvariant();
        var relativeDirectory = Path.Combine("录制提交", state.Id);
        var prefixRelativePath = Path.Combine(relativeDirectory, "prefixes", hash + ".bin");
        var prefixPath = Path.Combine(directory, prefixRelativePath);
        if (!IsMatchingBlob(prefixPath, hash, prefix.Length))
        {
            WriteAtomic(prefixPath, prefix);
        }
        var snapshot = new MediaRecordingCommitSnapshot
        {
            SegmentId = state.Id,
            RelativePath = state.RelativePath,
            IndexRelativePath = state.IndexRelativePath,
            CommitSequence = checked(state.CommitSequence + 1),
            MediaCommittedBytes = state.CommittedBytes,
            IndexCommittedBytes = state.IndexCommittedBytes,
            StartOffsetMs = state.StartOffsetMs,
            DurationMs = state.DurationMs,
            LastMediaReceivedAtUtc = state.LastMediaReceivedAtUtc,
            Codec = state.Codec,
            PrefixLength = prefix.Length,
            PrefixSha256 = hash,
            PrefixRelativePath = prefixRelativePath,
            IsClosed = isClosed
        };
        WriteAtomic(Path.Combine(directory, relativeDirectory, "latest.json"), JsonSerializer.SerializeToUtf8Bytes(snapshot, MediaJson.Options));
        state.CommitSequence = snapshot.CommitSequence;
        return snapshot;
    }

    private static byte[] ReadPrefix(FileStream stream, long committedBytes)
    {
        Span<byte> header = stackalloc byte[FLV_HEADER_LENGTH + TAG_HEADER_LENGTH];
        ReadExactly(stream, header, 0);
        if (!header[..3].SequenceEqual("FLV"u8) || header[3] != 1
            || BinaryPrimitives.ReadUInt32BigEndian(header[5..9]) != 9
            || BinaryPrimitives.ReadUInt32BigEndian(header[9..13]) != 0
            || header[FLV_HEADER_LENGTH] != 18)
        {
            throw new InvalidDataException("The recording does not start with a supported FLV metadata tag.");
        }
        var payloadLength = header[14] << 16 | header[15] << 8 | header[16];
        var prefixLength = checked(FLV_HEADER_LENGTH + TAG_HEADER_LENGTH + payloadLength + PREVIOUS_TAG_SIZE_LENGTH);
        if (prefixLength > committedBytes)
        {
            throw new InvalidDataException("The FLV metadata extends beyond the committed media boundary.");
        }
        var prefix = new byte[prefixLength];
        ReadExactly(stream, prefix, 0);
        if (BinaryPrimitives.ReadUInt32BigEndian(prefix.AsSpan(prefix.Length - PREVIOUS_TAG_SIZE_LENGTH)) != TAG_HEADER_LENGTH + payloadLength)
        {
            throw new InvalidDataException("The FLV metadata has an invalid previous-tag size.");
        }
        return prefix;
    }

    private static void ReadExactly(FileStream stream, Span<byte> destination, long offset)
    {
        while (!destination.IsEmpty)
        {
            var read = RandomAccess.Read(stream.SafeFileHandle, destination, offset);
            if (read == 0)
            {
                throw new EndOfStreamException("The committed FLV prefix is incomplete.");
            }
            offset += read;
            destination = destination[read..];
        }
    }

    private static bool IsMatchingBlob(string path, string hash, int length)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != length)
        {
            return false;
        }
        using var input = File.OpenRead(path);
        return string.Equals(Convert.ToHexString(SHA256.HashData(input)), hash, StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteAtomic(string path, ReadOnlySpan<byte> content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16384, FileOptions.WriteThrough))
            {
                output.Write(content);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
