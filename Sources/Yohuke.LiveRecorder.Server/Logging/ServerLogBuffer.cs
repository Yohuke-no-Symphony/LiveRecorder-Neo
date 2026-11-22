using System.Globalization;

namespace Yohuke.LiveRecorder.Server.Logging;

internal sealed class ServerLogBuffer
{
    private const int CAPACITY = 1000;
    private const int PAGE_SIZE = 200;
    private const int STREAM_ID_LENGTH = 32;
    private const int MAX_CURSOR_LENGTH = STREAM_ID_LENGTH + 1 + 19;

    private readonly Lock gate = new();
    private readonly Guid streamId = Guid.NewGuid();
    private readonly ServerLogEntry[] entries = new ServerLogEntry[CAPACITY];
    private long lastId;

    internal void Append(LogLevel level, string category, string message, string? exceptionType)
    {
        lock (gate)
        {
            var index = (int)(lastId % CAPACITY);
            entries[index] = new(++lastId, DateTimeOffset.UtcNow, level.ToString(), category, message, exceptionType);
        }
    }

    internal ServerLogPage? Read(string? cursor)
    {
        var requestedStreamId = streamId;
        var after = 0L;
        if (cursor is not null && !TryParseCursor(cursor, out requestedStreamId, out after))
        {
            return null;
        }

        lock (gate)
        {
            string? resetReason = null;
            if (requestedStreamId != streamId)
            {
                resetReason = "processRestarted";
            }
            else if (after > lastId)
            {
                return null;
            }
            else if (cursor is not null && after < lastId - CAPACITY)
            {
                resetReason = "bufferOverwritten";
            }

            if (cursor is null || resetReason is not null)
            {
                after = Math.Max(0, lastId - PAGE_SIZE);
            }

            var count = (int)Math.Min(lastId - after, PAGE_SIZE);
            var page = new ServerLogEntry[count];
            for (var index = 0; index < count; index++)
            {
                page[index] = entries[(int)((after + index) % CAPACITY)];
            }

            var readThrough = after + count;
            var nextCursor = string.Create(CultureInfo.InvariantCulture, $"{streamId:N}:{readThrough}");
            return new(page, nextCursor, readThrough < lastId, resetReason);
        }
    }

    private static bool TryParseCursor(ReadOnlySpan<char> cursor, out Guid cursorStreamId, out long after)
    {
        cursorStreamId = Guid.Empty;
        after = 0;
        return cursor.Length is > STREAM_ID_LENGTH + 1 and <= MAX_CURSOR_LENGTH
            && cursor[STREAM_ID_LENGTH] == ':'
            && Guid.TryParseExact(cursor[..STREAM_ID_LENGTH], "N", out cursorStreamId)
            && long.TryParse(cursor[(STREAM_ID_LENGTH + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out after);
    }
}
