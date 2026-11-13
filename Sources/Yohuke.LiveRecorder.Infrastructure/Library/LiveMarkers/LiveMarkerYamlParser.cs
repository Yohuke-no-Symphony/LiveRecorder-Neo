using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Library.LiveMarkers;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Domain.LiveMarkers;

namespace Yohuke.LiveRecorder.Infrastructure.Library.LiveMarkers;

/// <summary>有大小、节点和时区边界的 DanmuNeo 原生 YAML 解析器。</summary>
public sealed partial class LiveMarkerYamlParser
{
    /// <summary>独立读取原时间字段；旧无偏移日期按明确的来源时区解释。</summary>
    public LiveMarkerDocument Parse(ReadOnlySpan<byte> bytes, TimeZoneInfo sourceTimeZone)
    {
        if (bytes.Length > LiveMarkerLimits.MAXIMUM_BYTES)
        {
            throw new InfrastructureException(MessageCode.LIVE_MARKER_FILE_TOO_LARGE, 413);
        }

        try
        {
            var content = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
            ValidateYamlStructure(content);
            var yaml = new YamlStream();
            yaml.Load(new StringReader(content));
            if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode root)
            {
                throw InvalidFile();
            }

            ValidateFields(root, "StartTime", "Marker");
            var startTime = ReadDateTime(ReadScalar(root, "StartTime"), sourceTimeZone);
            if (root.Children[new YamlScalarNode("Marker")] is not YamlSequenceNode markers)
            {
                throw InvalidFile();
            }

            if (markers.Children.Count > LiveMarkerLimits.MAXIMUM_ENTRIES)
            {
                throw new InfrastructureException(MessageCode.LIVE_MARKER_TOO_MANY_ENTRIES);
            }

            var entries = new List<LiveMarkerEntry>(markers.Children.Count);
            foreach (var node in markers.Children)
            {
                if (node is not YamlMappingNode marker)
                {
                    throw InvalidFile();
                }

                ValidateFields(marker, "RealDateTime", "LiveTime", "Message", "MarkerColor");
                var colorText = ReadScalar(marker, "MarkerColor");
                if (!Enum.GetNames<LiveMarkerColor>().Contains(colorText, StringComparer.Ordinal) ||
                    !Enum.TryParse<LiveMarkerColor>(colorText, out var color))
                {
                    throw InvalidFile();
                }

                entries.Add(new()
                {
                    RealDateTime = ReadDateTime(ReadScalar(marker, "RealDateTime"), sourceTimeZone),
                    LiveTime = ReadDuration(ReadScalar(marker, "LiveTime")),
                    Message = ReadScalar(marker, "Message"),
                    MarkerColor = color
                });
            }

            return new() { StartTime = startTime, Entries = entries };
        }
        catch (Exception error) when (error is YamlException or DecoderFallbackException or ArgumentException
                                          or OverflowException or FormatException)
        {
            throw InvalidFile();
        }
    }

    /// <summary>验证文件校准不会超出可表示的实际时间。</summary>
    public static void ValidateAlignment(LiveMarkerDocument document, long calibrationMs)
    {
        if (calibrationMs is < -LiveMarkerLimits.MAXIMUM_CALIBRATION_MS or > LiveMarkerLimits.MAXIMUM_CALIBRATION_MS)
        {
            throw new InfrastructureException(MessageCode.LIVE_MARKER_ALIGNMENT_INVALID);
        }

        foreach (var entry in document.Entries)
        {
            var ticks = entry.RealDateTime.UtcTicks + (decimal)calibrationMs * TimeSpan.TicksPerMillisecond;
            if (ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks)
            {
                throw new InfrastructureException(MessageCode.LIVE_MARKER_ALIGNMENT_INVALID);
            }
        }
    }

    /// <summary>读取可跨机器使用的明确时区标识。</summary>
    public static TimeZoneInfo ResolveTimeZone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException
                                          or ArgumentException)
        {
            throw new InfrastructureException(MessageCode.LIVE_MARKER_TIME_ZONE_INVALID);
        }
    }

    private static void ValidateYamlStructure(string content)
    {
        var parser = new Parser(new StringReader(content));
        var depth = 0;
        var count = 0;
        while (parser.MoveNext())
        {
            if (++count > LiveMarkerLimits.MAXIMUM_ENTRIES * 14 + 100 || parser.Current is AnchorAlias)
            {
                throw InvalidFile();
            }

            if (parser.Current is MappingStart or SequenceStart)
            {
                if (++depth > 8)
                {
                    throw InvalidFile();
                }
            }
            else if (parser.Current is MappingEnd or SequenceEnd)
            {
                depth--;
            }
        }
    }

    private static DateTimeOffset ReadDateTime(string text, TimeZoneInfo sourceTimeZone)
    {
        var match = TimestampPattern().Match(text);
        if (!match.Success)
        {
            throw InvalidFile();
        }

        if (match.Groups["zone"].Success)
        {
            return DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.None);
        }

        var date = DateTime.SpecifyKind(DateTime.Parse(text, CultureInfo.InvariantCulture), DateTimeKind.Unspecified);
        if (sourceTimeZone.IsInvalidTime(date) || sourceTimeZone.IsAmbiguousTime(date))
        {
            throw new InfrastructureException(MessageCode.LIVE_MARKER_TIME_ZONE_AMBIGUOUS);
        }

        return new(date, sourceTimeZone.GetUtcOffset(date));
    }

    private static TimeSpan ReadDuration(string text)
    {
        if (!text.StartsWith('-') && TimeSpan.TryParseExact(text, "c", CultureInfo.InvariantCulture, out var legacy) &&
            legacy >= TimeSpan.Zero)
        {
            return legacy;
        }

        var match = DurationPattern().Match(text);
        if (!match.Success || !long.TryParse(match.Groups["hours"].Value, NumberStyles.None,
                CultureInfo.InvariantCulture, out var hours) ||
            !int.TryParse(match.Groups["minutes"].Value, out var minutes) || minutes >= 60 ||
            !int.TryParse(match.Groups["seconds"].Value, out var seconds) || seconds >= 60)
        {
            throw InvalidFile();
        }

        var fraction = match.Groups["fraction"].Value;
        var ticks = fraction.Length == 0 ? 0 : int.Parse(fraction.PadRight(7, '0'), CultureInfo.InvariantCulture);
        return TimeSpan.FromTicks(checked(hours * TimeSpan.TicksPerHour + minutes * TimeSpan.TicksPerMinute +
                                          seconds * TimeSpan.TicksPerSecond + ticks));
    }

    private static string ReadScalar(YamlMappingNode node, string name)
    {
        if (!node.Children.TryGetValue(new YamlScalarNode(name), out var value) || value is not YamlScalarNode scalar)
        {
            throw InvalidFile();
        }

        return scalar.Value ?? "";
    }

    private static void ValidateFields(YamlMappingNode node, params string[] names)
    {
        if (node.Children.Count != names.Length || node.Children.Keys.Any(key => key is not YamlScalarNode scalar ||
                scalar.Value is null || !names.Contains(scalar.Value, StringComparer.Ordinal)))
        {
            throw InvalidFile();
        }
    }

    private static InfrastructureException InvalidFile() => new(MessageCode.LIVE_MARKER_FILE_INVALID);

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}[Tt ]\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?<zone>[Zz]|[+\-]\d{2}:\d{2})?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex TimestampPattern();

    [GeneratedRegex(@"^(?<hours>\d+):(?<minutes>\d{2}):(?<seconds>\d{2})(?:\.(?<fraction>\d{1,7}))?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex DurationPattern();
}
