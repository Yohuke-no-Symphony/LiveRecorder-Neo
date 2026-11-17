using System.Globalization;
using System.Text.RegularExpressions;

namespace Yohuke.LiveRecorder.Transcoding.Contracts;

/// <summary>验证独立分片与单媒体文件的本地 fMP4 VOD 清单。</summary>
public static class TranscodePlaylistPolicy
{
    private const int MAX_ARTIFACTS = 100000;

    private static Regex FragmentName { get; } = new("^chunk_[0-9]{5,8}\\.m4s$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static Regex IntegerTag { get; } = new("^#EXT-X-(?:VERSION|TARGETDURATION|MEDIA-SEQUENCE):[0-9]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static Regex SingleFileMap { get; } = new("^#EXT-X-MAP:URI=\"media\\.mp4\",BYTERANGE=\"([0-9]+)@0\"$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static Regex ByteRange { get; } = new("^#EXT-X-BYTERANGE:([0-9]+)@([0-9]+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>仅允许当前转码格式使用的固定名称和编号分片。</summary>
    public static bool IsSupportedFileName(string? fileName) => fileName is "index.m3u8" or "init.mp4" or "media.mp4"
        || fileName is not null && FragmentName.IsMatch(fileName);

    /// <summary>解析完整播放清单，校验当前配方、已声明文件和单文件字节范围。</summary>
    public static TranscodePlaylist Parse(string text, TranscodeProfile profile, IReadOnlyDictionary<string, long> files,
        double? frameRate = null)
    {
        TranscodeConfigurationPolicy.ValidateProfile(profile);
        if (files.Count is < 2 or > MAX_ARTIFACTS || !files.ContainsKey("index.m3u8")
            || files.Keys.Any(name => !IsSupportedFileName(name))
            || profile.SingleFile && (files.Count != 2 || !files.ContainsKey("media.mp4"))
            || !profile.SingleFile && (files.Count < 3 || !files.ContainsKey("init.mp4") || files.ContainsKey("media.mp4")))
        {
            throw new InvalidDataException("The transcoded output files do not match the selected storage mode.");
        }
        if (frameRate is { } fps && (!double.IsFinite(fps) || fps <= 0))
        {
            throw new InvalidDataException("The transcoded frame rate must be finite and positive.");
        }
        var maximumDuration = frameRate is { } rate
            ? Math.Ceiling(profile.SegmentDurationSeconds / profile.KeyframeIntervalSeconds) * profile.KeyframeIntervalSeconds
                + Math.Max(0.5, 2 / rate)
            : double.PositiveInfinity;
        var lines = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 5 || lines[0] != "#EXTM3U")
        {
            throw new InvalidDataException("The transcoded output must contain a complete VOD playlist.");
        }
        var references = new HashSet<string>(StringComparer.Ordinal) { "index.m3u8" };
        var names = new List<string>();
        var durations = new List<double>();
        var hasMap = false;
        var hasVod = false;
        var ended = false;
        double? pendingDuration = null;
        long? pendingRangeLength = null;
        long previousRangeEnd = 0;
        foreach (var line in lines.Skip(1))
        {
            if (ended)
            {
                throw new InvalidDataException("The transcoded playlist contains data after its end marker.");
            }
            if (line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
            {
                if (hasMap || pendingDuration is not null)
                {
                    throw new InvalidDataException("The transcoded initialization map is repeated or misplaced.");
                }
                if (profile.SingleFile)
                {
                    var map = SingleFileMap.Match(line);
                    if (!map.Success || !long.TryParse(map.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var length)
                        || length <= 0 || length > files["media.mp4"])
                    {
                        throw new InvalidDataException("The transcoded single-file initialization range is invalid.");
                    }
                    previousRangeEnd = length;
                    references.Add("media.mp4");
                }
                else if (line == "#EXT-X-MAP:URI=\"init.mp4\"")
                {
                    references.Add("init.mp4");
                }
                else
                {
                    throw new InvalidDataException("The transcoded initialization map is invalid.");
                }
                hasMap = true;
            }
            else if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                if (pendingDuration is not null || !line.EndsWith(',')
                    || !double.TryParse(line.AsSpan(8, line.Length - 9), NumberStyles.Float, CultureInfo.InvariantCulture, out var duration)
                    || !double.IsFinite(duration) || duration <= 0 || duration > maximumDuration)
                {
                    throw new InvalidDataException("A transcoded segment duration is outside the selected profile range.");
                }
                pendingDuration = duration;
            }
            else if (line.StartsWith("#EXT-X-BYTERANGE:", StringComparison.Ordinal))
            {
                var range = ByteRange.Match(line);
                if (!profile.SingleFile || !hasMap || pendingDuration is null || pendingRangeLength is not null
                    || !range.Success || !long.TryParse(range.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var length)
                    || !long.TryParse(range.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var offset)
                    || length <= 0 || offset < previousRangeEnd || offset > files["media.mp4"] || length > files["media.mp4"] - offset)
                {
                    throw new InvalidDataException("A transcoded byte range is invalid, overlapping, or outside its media file.");
                }
                previousRangeEnd = offset + length;
                pendingRangeLength = length;
            }
            else if (!line.StartsWith('#'))
            {
                if (!hasMap || pendingDuration is null || !files.ContainsKey(line)
                    || profile.SingleFile && (line != "media.mp4" || pendingRangeLength is null)
                    || !profile.SingleFile && (!FragmentName.IsMatch(line) || !references.Add(line)))
                {
                    throw new InvalidDataException("The transcoded playlist contains an invalid local file reference.");
                }
                if (!profile.SingleFile || names.Count == 0)
                {
                    names.Add(line);
                }
                durations.Add(pendingDuration.Value);
                pendingDuration = null;
                pendingRangeLength = null;
            }
            else if (line == "#EXT-X-PLAYLIST-TYPE:VOD")
            {
                hasVod = true;
            }
            else if (line == "#EXT-X-ENDLIST")
            {
                ended = true;
            }
            else if (line != "#EXT-X-INDEPENDENT-SEGMENTS" && !IntegerTag.IsMatch(line))
            {
                throw new InvalidDataException("The transcoded playlist contains an unsupported tag.");
            }
        }
        if (!hasMap || !hasVod || !ended || pendingDuration is not null || pendingRangeLength is not null
            || durations.Count == 0 || !references.SetEquals(files.Keys))
        {
            throw new InvalidDataException("The transcoded playlist references do not match the declared output files.");
        }
        if (!profile.SingleFile)
        {
            names.Add("init.mp4");
        }
        names.Add("index.m3u8");
        return new() { FileNames = names, SegmentDurations = durations };
    }
}
