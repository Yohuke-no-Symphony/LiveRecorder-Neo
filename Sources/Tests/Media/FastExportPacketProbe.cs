using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Yohuke.LiveRecorder.Media.Tests;

internal static class FastExportPacketProbe
{
    internal static async Task<(SeamlessMediaPacket[] Packets, Dictionary<string, decimal> ClockTicksMs)> ReadAsync(string path)
    {
        var output = new StringBuilder();
        await MediaProcessRunner.RunProcessAsync("ffprobe",
        [
            "-v", "error", "-show_packets", "-show_streams", "-show_data", "-show_data_hash", "sha256",
            "-show_entries", "stream=index,codec_type,time_base:packet=stream_index,dts,pts,data,data_hash", "-of", "json", path
        ], CancellationToken.None, outputLine: line => output.AppendLine(line));
        using var document = JsonDocument.Parse(output.ToString());
        var streams = document.RootElement.GetProperty("streams").EnumerateArray().ToDictionary(
            stream => stream.GetProperty("index").GetInt32(), stream =>
            {
                var clock = stream.GetProperty("time_base").GetString()!.Split('/');
                return (Kind: stream.GetProperty("codec_type").GetString()!,
                    ClockTickMs: 1000m * decimal.Parse(clock[0], CultureInfo.InvariantCulture) / decimal.Parse(clock[1], CultureInfo.InvariantCulture));
            });
        var packets = document.RootElement.GetProperty("packets").EnumerateArray().Select(packet =>
        {
            var stream = streams[packet.GetProperty("stream_index").GetInt32()];
            var hash = stream.Kind == "video" ? VideoHash(Payload(packet.GetProperty("data").GetString()!))
                : packet.GetProperty("data_hash").GetString()!;
            return new SeamlessMediaPacket(stream.Kind, hash, packet.GetProperty("dts").GetInt64() * stream.ClockTickMs,
                packet.GetProperty("pts").GetInt64() * stream.ClockTickMs);
        }).ToArray();
        return (packets, streams.Values.ToDictionary(stream => stream.Kind, stream => stream.ClockTickMs, StringComparer.Ordinal));
    }

    private static byte[] Payload(string dump)
    {
        var hex = new StringBuilder();
        foreach (var line in dump.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = line.IndexOf(':');
            if (colon < 0)
            {
                continue;
            }
            var field = line[(colon + 1)..].TrimStart();
            var ascii = field.IndexOf("  ", StringComparison.Ordinal);
            if (ascii >= 0)
            {
                field = field[..ascii];
            }
            hex.Append(field.Replace(" ", "", StringComparison.Ordinal));
        }
        return Convert.FromHexString(hex.ToString());
    }

    private static string VideoHash(byte[] payload)
    {
        var units = LengthPrefixedUnits(payload) ?? AnnexBUnits(payload);
        using var normalized = new MemoryStream();
        Span<byte> length = stackalloc byte[4];
        var hasPicture = false;
        foreach (var unit in units)
        {
            var type = unit[0] & 31;
            if (type is 7 or 8 or 9)
            {
                continue;
            }
            hasPicture |= type is 1 or 5;
            BinaryPrimitives.WriteInt32BigEndian(length, unit.Length);
            normalized.Write(length);
            normalized.Write(unit);
        }
        if (!hasPicture)
        {
            throw new InvalidDataException("An H.264 media packet lost its coded picture during export.");
        }
        return Convert.ToHexStringLower(SHA256.HashData(normalized.ToArray()));
    }

    private static List<byte[]>? LengthPrefixedUnits(byte[] payload)
    {
        var units = new List<byte[]>();
        var offset = 0;
        while (offset < payload.Length)
        {
            if (payload.Length - offset < 4)
            {
                return null;
            }
            var size = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(offset, 4));
            offset += 4;
            if (size == 0 || size > payload.Length - offset)
            {
                return null;
            }
            units.Add(payload.AsSpan(offset, checked((int)size)).ToArray());
            offset += checked((int)size);
        }
        return units;
    }

    private static List<byte[]> AnnexBUnits(byte[] payload)
    {
        var units = new List<byte[]>();
        var (start, prefix) = StartCode(payload, 0);
        if (start < 0)
        {
            throw new InvalidDataException("An H.264 packet has neither AVCC lengths nor Annex B start codes.");
        }
        while (start >= 0)
        {
            var data = start + prefix;
            var (next, nextPrefix) = StartCode(payload, data);
            var end = next < 0 ? payload.Length : next;
            while (end > data && payload[end - 1] == 0)
            {
                end--;
            }
            if (end > data)
            {
                units.Add(payload.AsSpan(data, end - data).ToArray());
            }
            start = next;
            prefix = nextPrefix;
        }
        return units;
    }

    private static (int Offset, int Length) StartCode(byte[] payload, int offset)
    {
        for (var index = offset; index + 2 < payload.Length; index++)
        {
            if (payload[index] == 0 && payload[index + 1] == 0)
            {
                if (payload[index + 2] == 1)
                {
                    return (index, 3);
                }
                if (index + 3 < payload.Length && payload[index + 2] == 0 && payload[index + 3] == 1)
                {
                    return (index, 4);
                }
            }
        }
        return (-1, 0);
    }
}
