using Microsoft.Extensions.Options;
using Yohuke.LiveRecorder.Application.Media;

namespace Yohuke.LiveRecorder.Media;

/// <summary>复用媒体进程限流，从有限的已提交关键帧范围生成封面。</summary>
public sealed class FfmpegVideoFrameExtractor(MediaProcessRunner processes, IOptions<MediaToolOptions> options) : IVideoFrameExtractor
{
    /// <inheritdoc />
    public async Task<bool> TryExtractAsync(VideoFrameRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = Path.GetFullPath(request.SourcePath);
        var destination = Path.GetFullPath(request.OutputPath);
        if (string.Equals(source, destination, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new ArgumentException("A frame cannot overwrite its source recording.", nameof(request));
        }
        if (!File.Exists(source))
        {
            return false;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Value.FrameExtractionTimeout);
        var token = timeout.Token;
        var sourceLength = new FileInfo(source).Length;
        var committed = request.CommittedBytes > 0 ? Math.Min(request.CommittedBytes, sourceLength) : request.IsClosed ? sourceLength : 0;
        if (committed <= 13)
        {
            return false;
        }
        Func<Stream, CancellationToken, Task>? input = null;
        var hasIndex = File.Exists(request.IndexPath);
        if (hasIndex)
        {
            var index = await FlvIndexSnapshot.ReadAsync(request.IndexPath, committed, token);
            var first = index.Keyframes.FirstOrDefault();
            if (first is null)
            {
                return false;
            }
            var endOffset = index.Keyframes.FirstOrDefault(frame => frame.Offset > first.Offset)?.Offset ?? committed;
            input = (stream, ct) => IndexedFlvRange.CopyAsync(source, index, first, endOffset, stream, ct);
        }
        else if (!request.IsClosed)
        {
            return false;
        }
        else if (request.CommittedBytes > 0)
        {
            input = (stream, ct) => CopyCommittedAsync(source, committed, stream, ct);
        }
        var directory = Path.GetDirectoryName(destination)!;
        var temporary = Path.Combine(directory, ".frame-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var output = Path.Combine(temporary, "frame.jpg");
            var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
            if (hasIndex)
            {
                arguments.AddRange(["-f", "flv"]);
            }
            else if (input is null)
            {
                arguments.AddRange(["-protocol_whitelist", "file"]);
            }
            arguments.AddRange([
                "-i", input is null ? source : "pipe:0", "-map", "0:v:0", "-an", "-frames:v", "1",
                "-vf", "scale=w='min(640,iw)':h=-2", "-c:v", "mjpeg", "-q:v", "3", "-f", "image2", "-update", "1", output
            ]);
            await processes.RunAsync(MediaProcessKind.Export, arguments, token, input);
            if (!File.Exists(output) || new FileInfo(output).Length == 0)
            {
                return false;
            }
            token.ThrowIfCancellationRequested();
            File.Move(output, destination, overwrite: true);
            return true;
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }

    private static async Task CopyCommittedAsync(string path, long length, Stream output, CancellationToken cancellationToken)
    {
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, FileOptions.Asynchronous);
        var buffer = new byte[65536];
        while (length > 0)
        {
            var count = (int)Math.Min(buffer.Length, length);
            await source.ReadExactlyAsync(buffer.AsMemory(0, count), cancellationToken);
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            length -= count;
        }
    }
}
