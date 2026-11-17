using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Recording.Contracts;

namespace Yohuke.LiveRecorder.Infrastructure.Recording;

internal static class RecordingIngestValidation
{
    private const int MAX_INDEX_LINE_CHARACTERS = 32 * 1024 * 1024;

    internal static async Task ValidateIndexAsync(string path, RecordingSegmentCommit commit, CancellationToken cancellationToken)
    {
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, true);
        if (source.Length < commit.IndexCommittedBytes)
        {
            throw new InfrastructureException("录像索引尚未上传完整。", 409);
        }
        var reader = PipeReader.Create(source, new StreamPipeReaderOptions(bufferSize: 65536, leaveOpen: true));
        long consumed = 0;
        long lastOffset = -1;
        double lastDuration = -1;
        try
        {
            while (consumed < commit.IndexCommittedBytes)
            {
                var result = await reader.ReadAsync(cancellationToken);
                var buffer = result.Buffer.Slice(0, Math.Min(result.Buffer.Length, commit.IndexCommittedBytes - consumed));
                try
                {
                    while (buffer.PositionOf((byte)'\n') is { } newline)
                    {
                        var lineBytes = buffer.Slice(0, newline);
                        consumed += lineBytes.Length + 1;
                        buffer = buffer.Slice(buffer.GetPosition(1, newline));
                        var line = Encoding.UTF8.GetString(lineBytes).TrimEnd('\r');
                        if (line.Length > MAX_INDEX_LINE_CHARACTERS)
                        {
                            throw new InfrastructureException("录像索引提交边界无效。", 422);
                        }
                        using var document = JsonDocument.Parse(line);
                        var root = document.RootElement;
                        var kind = root.GetProperty("kind").GetString();
                        var offset = root.GetProperty("offset").GetInt64();
                        if (offset < 0 || offset > commit.MediaCommittedBytes)
                        {
                            throw new InfrastructureException("录像索引超出已提交媒体范围。", 422);
                        }
                        if (kind == "Committed")
                        {
                            lastOffset = offset;
                            lastDuration = root.GetProperty("timestampMs").GetDouble();
                        }
                        else
                        {
                            var size = root.TryGetProperty("size", out var sizeValue) ? sizeValue.GetInt64() : 0;
                            if (size is < 0 or > 0xFFFFFF || offset + 15 + size > commit.MediaCommittedBytes)
                            {
                                throw new InfrastructureException("录像索引包含不完整媒体标签。", 422);
                            }
                        }
                    }
                    if (buffer.Length > (MAX_INDEX_LINE_CHARACTERS + 1L) * 3)
                    {
                        throw new InfrastructureException("录像索引提交边界无效。", 422);
                    }
                    if (consumed < commit.IndexCommittedBytes
                        && (result.IsCompleted || consumed + buffer.Length == commit.IndexCommittedBytes))
                    {
                        throw new InfrastructureException("录像索引提交边界不完整。", 422);
                    }
                }
                finally
                {
                    reader.AdvanceTo(buffer.Start, buffer.End);
                }
            }
        }
        finally
        {
            await reader.CompleteAsync();
        }
        if (consumed != commit.IndexCommittedBytes || lastOffset != commit.MediaCommittedBytes || !double.IsFinite(lastDuration)
            || Math.Abs(lastDuration - commit.DurationMs) > 1)
        {
            throw new InfrastructureException("录像媒体与索引提交水位不一致。", 422);
        }
    }

    internal static void ValidateCommit(RecordingSegmentCommit commit)
    {
        RecordingIngestPaths.RequireId(commit.SessionId);
        RecordingIngestPaths.RequireId(commit.SegmentId);
        RecordingIngestPaths.RequireHash(commit.PrefixSha256);
        if (commit.CommitSequence <= 0 || commit.PrefixLength is < 28 or > 32 * 1024 * 1024
            || commit.MediaCommittedBytes < commit.PrefixLength || commit.IndexCommittedBytes <= 0
            || !double.IsFinite(commit.StartOffsetMs) || commit.StartOffsetMs < 0
            || !double.IsFinite(commit.DurationMs) || commit.DurationMs < 0 || commit.Codec is not ("avc" or "hevc"))
        {
            throw new InfrastructureException("录像媒体提交快照无效。");
        }
    }

    internal static void ValidatePrefix(ReadOnlySpan<byte> prefix)
    {
        if (prefix.Length < 28 || !prefix[..3].SequenceEqual("FLV"u8) || prefix[3] != 1
            || BinaryPrimitives.ReadUInt32BigEndian(prefix[5..9]) != 9 || BinaryPrimitives.ReadUInt32BigEndian(prefix[9..13]) != 0
            || prefix[13] != 18)
        {
            throw new InfrastructureException("录像前缀不是完整FLV metadata。", 422);
        }
        var payload = prefix[14] << 16 | prefix[15] << 8 | prefix[16];
        if (prefix.Length != 13 + 11 + payload + 4 || BinaryPrimitives.ReadUInt32BigEndian(prefix[^4..]) != 11 + payload)
        {
            throw new InfrastructureException("录像前缀越过固定metadata边界。", 422);
        }
    }
}
