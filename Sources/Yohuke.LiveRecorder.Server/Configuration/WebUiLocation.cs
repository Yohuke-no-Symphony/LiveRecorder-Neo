using Yohuke.LiveRecorder.Application.Localization;
namespace Yohuke.LiveRecorder.Server.Configuration;

/// <summary>公开静态资源与私有档案之间的路径边界。</summary>
public sealed class WebUiLocation(string path)
{
    public string Path { get; } = System.IO.Path.GetFullPath(path);

    /// <summary>拒绝将可写私有目录放进公开 WebUI。</summary>
    public void EnsurePrivate(string privatePath)
    {
        if (string.IsNullOrWhiteSpace(privatePath) || !System.IO.Path.IsPathRooted(privatePath))
        {
            throw new Yohuke.LiveRecorder.Application.InfrastructureException(MessageCode.PATH_ABSOLUTE_REQUIRED);
        }
        var candidate = System.IO.Path.GetFullPath(privatePath);
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var root = System.IO.Path.TrimEndingDirectorySeparator(Path);
        if (candidate.Equals(root, comparison) || candidate.StartsWith(root + System.IO.Path.DirectorySeparatorChar, comparison))
        {
            throw new Yohuke.LiveRecorder.Application.InfrastructureException(MessageCode.PATH_WEBUI_OVERLAP);
        }
    }
}
