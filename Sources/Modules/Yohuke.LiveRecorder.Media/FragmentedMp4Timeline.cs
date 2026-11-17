using System.Buffers.Binary;

namespace Yohuke.LiveRecorder.Media;

internal static class FragmentedMp4Timeline
{
    private const int MAX_BOX_BYTES = 16 * 1024 * 1024;

    internal static async Task<(double PresentationMs, double DurationMs)> ReadAsync(string directory, CancellationToken cancellationToken)
    {
        using var playlist = File.OpenText(Path.Combine(directory, "index.m3u8"));
        string? initializationName = null;
        while (await playlist.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
            {
                continue;
            }
            initializationName = line switch
            {
                "#EXT-X-MAP:URI=\"init.mp4\"" => "init.mp4",
                _ when line.StartsWith("#EXT-X-MAP:URI=\"media.mp4\",BYTERANGE=\"", StringComparison.Ordinal) => "media.mp4",
                _ => throw new InvalidDataException("The transcoded initialization map is unsupported.")
            };
            break;
        }
        if (initializationName is null)
        {
            throw new InvalidDataException("The transcoded playlist has no initialization map.");
        }
        var initialization = await ReadBoxAsync(Path.Combine(directory, initializationName), "moov", cancellationToken);
        var fragmentName = initializationName == "media.mp4" ? initializationName : "chunk_00000.m4s";
        var fragment = await ReadBoxAsync(Path.Combine(directory, fragmentName), "moof", cancellationToken);
        return Read(initialization, fragment);
    }

    internal static (double PresentationMs, double DurationMs) Read(ReadOnlyMemory<byte> movie, ReadOnlyMemory<byte> fragment)
    {
        uint videoId = 0;
        uint timescale = 0;
        foreach (var track in Children(movie, "trak"))
        {
            if (Children(track, "edts").Any(edit => Children(edit, "elst").Any()))
            {
                throw new InvalidDataException("Transcoded MP4 must use an explicit timeline without edit lists.");
            }
            var media = Required(track, "mdia");
            var handler = Required(media, "hdlr");
            if (handler.Length < 12 || !handler.Span.Slice(8, 4).SequenceEqual("vide"u8))
            {
                continue;
            }
            var header = Required(track, "tkhd");
            var mediaHeader = Required(media, "mdhd");
            videoId = Unsigned(header.Span, header.Span[0] == 1 ? 20 : 12);
            timescale = Unsigned(mediaHeader.Span, mediaHeader.Span[0] == 1 ? 20 : 12);
        }
        if (videoId == 0 || timescale == 0)
        {
            throw new InvalidDataException("Transcoded MP4 has no timed video track.");
        }
        uint defaultDuration = 0;
        foreach (var extensions in Children(movie, "mvex"))
        {
            foreach (var defaults in Children(extensions, "trex"))
            {
                if (Unsigned(defaults.Span, 4) == videoId)
                {
                    defaultDuration = Unsigned(defaults.Span, 12);
                }
            }
        }
        foreach (var track in Children(fragment, "traf"))
        {
            var header = Required(track, "tfhd");
            if (Unsigned(header.Span, 4) != videoId)
            {
                continue;
            }
            var headerFlags = Unsigned(header.Span, 0) & 0xffffff;
            var headerOffset = 8 + ((headerFlags & 1) != 0 ? 8 : 0) + ((headerFlags & 2) != 0 ? 4 : 0);
            if ((headerFlags & 8) != 0)
            {
                defaultDuration = Unsigned(header.Span, headerOffset);
            }
            var decode = Required(track, "tfdt");
            var decodeTime = decode.Span[0] == 1 ? BinaryPrimitives.ReadUInt64BigEndian(Slice(decode.Span, 4, 8)) : Unsigned(decode.Span, 4);
            var run = Required(track, "trun");
            if (Unsigned(run.Span, 4) == 0)
            {
                throw new InvalidDataException("Transcoded MP4 has an empty initial video run.");
            }
            var flags = Unsigned(run.Span, 0) & 0xffffff;
            var offset = 8 + ((flags & 1) != 0 ? 4 : 0) + ((flags & 4) != 0 ? 4 : 0);
            var duration = (flags & 0x100) != 0 ? Unsigned(run.Span, offset) : defaultDuration;
            offset += ((flags & 0x100) != 0 ? 4 : 0) + ((flags & 0x200) != 0 ? 4 : 0) + ((flags & 0x400) != 0 ? 4 : 0);
            var composition = (flags & 0x800) == 0 ? 0L : run.Span[0] == 1
                ? BinaryPrimitives.ReadInt32BigEndian(Slice(run.Span, offset, 4)) : Unsigned(run.Span, offset);
            if (duration == 0 || decodeTime > 1UL << 53)
            {
                throw new InvalidDataException("Transcoded MP4 has invalid initial timing.");
            }
            return ((decodeTime + (double)composition) * 1000 / timescale, duration * 1000d / timescale);
        }
        throw new InvalidDataException("Transcoded MP4 has no initial video fragment.");
    }

    private static async Task<byte[]> ReadBoxAsync(string path, string type, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        var header = new byte[16];
        while (stream.Position < stream.Length)
        {
            var start = stream.Position;
            await stream.ReadExactlyAsync(header.AsMemory(0, 8), cancellationToken);
            long length = BinaryPrimitives.ReadUInt32BigEndian(header);
            var headerLength = 8;
            if (length == 1)
            {
                await stream.ReadExactlyAsync(header.AsMemory(8, 8), cancellationToken);
                var extended = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8));
                if (extended > long.MaxValue)
                {
                    throw new InvalidDataException("Invalid MP4 box length.");
                }
                length = (long)extended;
                headerLength = 16;
            }
            else if (length == 0)
            {
                length = stream.Length - start;
            }
            if (length < headerLength || length > stream.Length - start)
            {
                throw new InvalidDataException("Truncated MP4 box.");
            }
            if (IsType(header.AsSpan(4, 4), type))
            {
                if (length - headerLength > MAX_BOX_BYTES)
                {
                    throw new InvalidDataException("MP4 timing metadata exceeds its size limit.");
                }
                var body = new byte[(int)(length - headerLength)];
                await stream.ReadExactlyAsync(body, cancellationToken);
                return body;
            }
            stream.Position = start + length;
        }
        throw new InvalidDataException("Required MP4 timing box is missing.");
    }

    private static IEnumerable<ReadOnlyMemory<byte>> Children(ReadOnlyMemory<byte> parent, string type)
    {
        var offset = 0;
        while (offset < parent.Length)
        {
            var length = Unsigned(parent.Span, offset);
            var headerLength = 8;
            if (length == 1)
            {
                var extended = BinaryPrimitives.ReadUInt64BigEndian(Slice(parent.Span, offset + 8, 8));
                if (extended > int.MaxValue)
                {
                    throw new InvalidDataException("MP4 child box exceeds its parent.");
                }
                length = (uint)extended;
                headerLength = 16;
            }
            else if (length == 0)
            {
                length = (uint)(parent.Length - offset);
            }
            if (length < headerLength || length > parent.Length - offset)
            {
                throw new InvalidDataException("Truncated MP4 child box.");
            }
            if (IsType(Slice(parent.Span, offset + 4, 4), type))
            {
                yield return parent.Slice(offset + headerLength, (int)length - headerLength);
            }
            offset += (int)length;
        }
    }

    private static ReadOnlyMemory<byte> Required(ReadOnlyMemory<byte> parent, string type) =>
        Children(parent, type).FirstOrDefault() is { Length: > 0 } value ? value : throw new InvalidDataException("Required MP4 box is missing.");

    private static uint Unsigned(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32BigEndian(Slice(bytes, offset, 4));

    private static ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> bytes, int offset, int length) =>
        offset >= 0 && offset <= bytes.Length - length ? bytes.Slice(offset, length) : throw new InvalidDataException("Truncated MP4 timing metadata.");

    private static bool IsType(ReadOnlySpan<byte> bytes, string type) =>
        bytes.Length == 4 && type.Length == 4 && bytes[0] == type[0] && bytes[1] == type[1] && bytes[2] == type[2] && bytes[3] == type[3];
}
