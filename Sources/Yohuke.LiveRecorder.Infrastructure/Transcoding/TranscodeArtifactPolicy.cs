using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Infrastructure.Transcoding;

/// <summary>限制上传文件和 HLS 引用只能指向当前已验证成品集合。</summary>
public static class TranscodeArtifactPolicy
{
    /// <summary>拒绝路径、外部地址和非约定的文件名。</summary>
    public static void ValidateName(string fileName)
    {
        if (!TranscodePlaylistPolicy.IsSupportedFileName(fileName))
        {
            throw new InfrastructureException("转码文件名无效。");
        }
    }

    /// <summary>在媒体探测前限制播放清单，禁止外部输入与未经提交的文件引用。</summary>
    public static async Task ValidatePlaylistAsync(string directory, IReadOnlyList<TranscodeArtifact> files,
        CancellationToken cancellationToken, TranscodeProfile? profile = null, double? frameRate = null)
    {
        var playlist = Path.Combine(directory, "index.m3u8");
        if (new FileInfo(playlist).Length > 16 * 1024 * 1024)
        {
            throw new InfrastructureException("播放清单过大。");
        }
        var names = files.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        if (names.Count != files.Count)
        {
            throw new InfrastructureException("转码完成清单缺少必要文件或包含重复文件。");
        }
        var text = await File.ReadAllTextAsync(playlist, cancellationToken);
        try
        {
            TranscodePlaylistPolicy.Parse(text, profile ?? new(),
                files.ToDictionary(x => x.Name, x => x.Length, StringComparer.Ordinal), frameRate);
        }
        catch (InvalidDataException exception)
        {
            throw new InfrastructureException(exception.Message);
        }
    }
}
