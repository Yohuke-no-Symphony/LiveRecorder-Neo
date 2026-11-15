using System.Globalization;
using System.Text.RegularExpressions;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Infrastructure.Transcoding;

/// <summary>限制上传文件和 HLS 引用只能指向当前已验证成品集合。</summary>
public static partial class TranscodeArtifactPolicy
{
    /// <summary>拒绝路径、外部地址和非约定的文件名。</summary>
    public static void ValidateName(string fileName)
    {
        if (string.IsNullOrEmpty(fileName) || fileName.Length > 128
            || fileName is not ("index.m3u8" or "init.mp4") && !FragmentName().IsMatch(fileName))
        {
            throw new InfrastructureException("转码文件名无效。");
        }
    }

    /// <summary>在媒体探测前限制播放清单，禁止外部输入与未经提交的文件引用。</summary>
    public static async Task ValidatePlaylistAsync(string directory, IReadOnlyList<TranscodeArtifact> files,
        CancellationToken cancellationToken)
    {
        var playlist = Path.Combine(directory, "index.m3u8");
        if (new FileInfo(playlist).Length > 16 * 1024 * 1024)
        {
            throw new InfrastructureException("播放清单过大。");
        }
        var names = files.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        if (!names.Contains("index.m3u8") || !names.Contains("init.mp4") || names.Count != files.Count)
        {
            throw new InfrastructureException("转码完成清单缺少必要文件或包含重复文件。");
        }
        var lines = await File.ReadAllLinesAsync(playlist, cancellationToken);
        if (lines.Length < 5 || lines[0] != "#EXTM3U" || !lines.Contains("#EXT-X-ENDLIST"))
        {
            throw new InfrastructureException("转码产物必须是完整的 VOD 清单。");
        }
        var references = new HashSet<string>(StringComparer.Ordinal) { "index.m3u8", "init.mp4" };
        var hasMap = false;
        var pendingDuration = false;
        foreach (var line in lines.Skip(1).Where(x => x.Length > 0))
        {
            if (line == "#EXT-X-MAP:URI=\"init.mp4\"")
            {
                hasMap = true;
            }
            else if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                if (pendingDuration || !double.TryParse(line.AsSpan(8).TrimEnd(','), CultureInfo.InvariantCulture, out var duration)
                    || !double.IsFinite(duration) || duration <= 0 || duration > 60)
                {
                    throw new InfrastructureException("转码分片时长无效。");
                }
                pendingDuration = true;
            }
            else if (!line.StartsWith('#'))
            {
                ValidateName(line);
                if (!hasMap || !pendingDuration || !FragmentName().IsMatch(line) || !names.Contains(line) || !references.Add(line))
                {
                    throw new InfrastructureException("转码清单引用无效。");
                }
                pendingDuration = false;
            }
            else if (line is not ("#EXT-X-ENDLIST" or "#EXT-X-INDEPENDENT-SEGMENTS" or "#EXT-X-PLAYLIST-TYPE:VOD")
                && !IntegerTag().IsMatch(line))
            {
                throw new InfrastructureException("转码清单包含不支持的标签。");
            }
        }
        if (!hasMap || pendingDuration || references.Count < 3 || !references.SetEquals(names))
        {
            throw new InfrastructureException("转码完成清单与实际播放引用不一致。");
        }
    }

    [GeneratedRegex("^chunk_[0-9]{5,8}\\.m4s$", RegexOptions.CultureInvariant)]
    private static partial Regex FragmentName();

    [GeneratedRegex("^#EXT-X-(?:VERSION|TARGETDURATION|MEDIA-SEQUENCE):[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex IntegerTag();
}
