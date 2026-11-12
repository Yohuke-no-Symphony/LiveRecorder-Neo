using System.Globalization;
using System.Text;
using Yohuke.LiveRecorder.Application;

namespace Yohuke.LiveRecorder.Infrastructure.Files;

/// <summary>跨平台可读文件名与受约束路径解析。</summary>
public static class MediaPaths
{
    private static readonly HashSet<string> reservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>生成兼容三个操作系统的 Unicode 文件名。</summary>
    public static string SafeName(string value, int maximumLength = 90)
    {
        var builder = new StringBuilder();
        var encodedBytes = 0;
        var elements = StringInfo.GetTextElementEnumerator(value.Normalize(NormalizationForm.FormC));
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            if (builder.Length + element.Length > maximumLength || encodedBytes + Encoding.UTF8.GetByteCount(element) > 120)
            {
                break;
            }
            if (element.Any(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c)))
            {
                builder.Append('_');
                encodedBytes++;
            }
            else
            {
                builder.Append(element);
                encodedBytes += Encoding.UTF8.GetByteCount(element);
            }
            if (builder.Length >= maximumLength)
            {
                break;
            }
        }
        var name = builder.ToString().Trim().TrimEnd('.', ' ');
        if (string.IsNullOrEmpty(name))
        {
            name = "未命名";
        }
        if (reservedNames.Contains(name.Split('.')[0]))
        {
            name = "_" + name;
        }
        return name;
    }

    /// <summary>解析存储根内的相对路径，拒绝越界和符号链接。</summary>
    public static string Resolve(string root, string relativePath)
    {
        var fullRoot = Path.GetFullPath(root);
        if (Path.IsPathRooted(relativePath))
        {
            throw new InfrastructureException("不允许绝对媒体路径。", 400);
        }
        var result = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!result.StartsWith(Path.TrimEndingDirectorySeparator(fullRoot) + Path.DirectorySeparatorChar, comparison))
        {
            throw new InfrastructureException("媒体路径超出存储目录。", 400);
        }
        var current = Path.GetDirectoryName(result);
        while (current is not null && !string.Equals(current, fullRoot, comparison))
        {
            if (Directory.Exists(current) && new DirectoryInfo(current).LinkTarget is not null)
            {
                throw new InfrastructureException("媒体目录不允许符号链接。", 400);
            }
            current = Path.GetDirectoryName(current);
        }
        if (File.Exists(result) && new FileInfo(result).LinkTarget is not null)
        {
            throw new InfrastructureException("媒体文件不允许符号链接。", 400);
        }
        return result;
    }

    /// <summary>解析原始片段或归组片段的稳定文件引用。</summary>
    public static string ResolveSegment(string storageRoot, string sessionDirectory, Yohuke.LiveRecorder.Domain.RecordingSegment segment)
    {
        return Resolve(storageRoot, segment.RootRelativePath ?? Path.Combine(sessionDirectory, segment.RelativePath));
    }

    /// <summary>解析片段索引的稳定文件引用。</summary>
    public static string ResolveSegmentIndex(string storageRoot, string sessionDirectory, Yohuke.LiveRecorder.Domain.RecordingSegment segment)
    {
        var path = segment.RootIndexRelativePath ?? (string.IsNullOrEmpty(segment.IndexRelativePath) ? "" : Path.Combine(sessionDirectory, segment.IndexRelativePath));
        return string.IsNullOrEmpty(path) ? "" : Resolve(storageRoot, path);
    }
}
