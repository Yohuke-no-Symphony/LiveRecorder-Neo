using System.Security.Cryptography;
using System.Text.Json;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Files;

namespace Yohuke.LiveRecorder.Infrastructure.Playback;

internal static class TimelineArchiveReader
{
    private const int BUFFER_BYTES = 65536;
    private const int SIGNATURE_BYTES = 4096;
    private const int MAX_LINE_BYTES = 16 * 1024 * 1024;

    internal static async Task<TimelineSourceState> UpdateAsync(string path, EventArchiveSource source, long sequencePrefix, TimelineSourceState previous, CancellationToken cancellationToken)
    {
        await using var input = Open(path);
        if (input is null)
        {
            return previous.Position == 0 && previous.ObservedLength == 0 ? previous : new();
        }
        var snapshotLength = input.Length;
        var writeTicks = File.GetLastWriteTimeUtc(path).Ticks;
        var state = await IsCurrentAsync(input, previous, snapshotLength, writeTicks, cancellationToken) ? previous : new TimelineSourceState();
        input.Position = state.Position;
        var buffer = new byte[BUFFER_BYTES];
        using var line = new MemoryStream();
        var position = state.Position;
        var oversized = false;
        while (position < snapshotLength)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, snapshotLength - position)), cancellationToken);
            if (read == 0)
            {
                break;
            }
            var fragment = 0;
            for (var index = 0; index < read; index++)
            {
                if (buffer[index] != '\n')
                {
                    continue;
                }
                Append(line, buffer, fragment, index - fragment, ref oversized);
                if (!oversized && Deserialize(line) is { } item && item.Sequence > state.LastSequence)
                {
                    TimelineProjectionAccumulator.Add(state, source, item, sequencePrefix);
                    state.LastSequence = item.Sequence;
                }
                state.Position = position + index + 1;
                fragment = index + 1;
                oversized = false;
                line.SetLength(0);
            }
            Append(line, buffer, fragment, read - fragment, ref oversized);
            position += read;
        }
        state.ObservedLength = snapshotLength;
        state.LastWriteUtcTicks = writeTicks;
        state.PrefixHash = await SignatureAsync(input, 0, Math.Min(state.Position, SIGNATURE_BYTES), cancellationToken);
        state.TailHash = await SignatureAsync(input, Math.Max(0, state.Position - SIGNATURE_BYTES), Math.Min(state.Position, SIGNATURE_BYTES), cancellationToken);
        return state;
    }

    private static async Task<bool> IsCurrentAsync(FileStream input, TimelineSourceState state, long length, long writeTicks, CancellationToken cancellationToken)
    {
        if (state.Position < 0 || state.ObservedLength > length || state.Position > length || state.LastWriteUtcTicks > writeTicks
            || state.ObservedLength == length && state.LastWriteUtcTicks != 0 && state.LastWriteUtcTicks != writeTicks)
        {
            return false;
        }
        if (state.Position == 0)
        {
            return true;
        }
        return state.PrefixHash == await SignatureAsync(input, 0, Math.Min(state.Position, SIGNATURE_BYTES), cancellationToken)
            && state.TailHash == await SignatureAsync(input, Math.Max(0, state.Position - SIGNATURE_BYTES), Math.Min(state.Position, SIGNATURE_BYTES), cancellationToken);
    }

    private static async Task<string> SignatureAsync(FileStream input, long offset, long length, CancellationToken cancellationToken)
    {
        input.Position = offset;
        var bytes = new byte[(int)length];
        await input.ReadExactlyAsync(bytes, cancellationToken);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static void Append(MemoryStream line, byte[] bytes, int offset, int count, ref bool oversized)
    {
        if (oversized || line.Length + count > MAX_LINE_BYTES)
        {
            oversized = true;
            return;
        }
        line.Write(bytes, offset, count);
    }

    private static RecordedEventEnvelope? Deserialize(MemoryStream line)
    {
        try
        {
            var item = JsonSerializer.Deserialize<RecordedEventEnvelope>(line.GetBuffer().AsSpan(0, (int)line.Length), JsonFiles.Options);
            return item is { Sequence: > 0 } && double.IsFinite(item.OffsetMs) ? item : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static FileStream? Open(string path)
    {
        try
        {
            return new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BUFFER_BYTES, true);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }
}
