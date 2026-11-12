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
            throw new Yohuke.LiveRecorder.Application.InfrastructureException("请填写有效的绝对目录。");
        }
        var candidate = System.IO.Path.GetFullPath(privatePath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var root = System.IO.Path.TrimEndingDirectorySeparator(Path);
        if (candidate.Equals(root, comparison) || candidate.StartsWith(root + System.IO.Path.DirectorySeparatorChar, comparison))
        {
            throw new Yohuke.LiveRecorder.Application.InfrastructureException("录播和服务数据目录必须位于 WebUI 目录之外。");
        }
    }
}
