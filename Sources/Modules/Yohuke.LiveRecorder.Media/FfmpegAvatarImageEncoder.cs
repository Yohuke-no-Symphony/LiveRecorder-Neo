using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Application.Media;

namespace Yohuke.LiveRecorder.Media;

internal sealed class FfmpegAvatarImageEncoder(MediaProcessRunner processes) : IAvatarImageEncoder
{
    public async Task<byte[]> EncodeAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken)
    {
        if (input.Length > IAvatarImageEncoder.MAX_IMAGE_BYTES || !input.Span.StartsWith<byte>([0xff, 0xd8, 0xff]))
        {
            throw new InfrastructureException(MessageCode.AUTH_AVATAR_INVALID);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var directory = Directory.CreateTempSubdirectory("live-recorder-avatar-");
        try
        {
            var output = Path.Combine(directory.FullName, "avatar.jpg");
            await processes.RunAsync(MediaProcessKind.Export, [
                "-hide_banner", "-loglevel", "error", "-nostdin", "-xerror",
                "-f", "image2pipe", "-protocol_whitelist", "pipe", "-c:v", "mjpeg", "-max_pixels", "262144", "-i", "pipe:0",
                "-map_metadata", "-1", "-vf", "crop='min(iw,ih)':'min(iw,ih)',scale=256:256", "-frames:v", "1",
                "-c:v", "mjpeg", "-q:v", "4", "-f", "image2", "-update", "1", output
            ], timeout.Token, (stream, token) => stream.WriteAsync(input, token).AsTask());

            var result = new FileInfo(output);
            if (!result.Exists || result.Length is 0 or > IAvatarImageEncoder.MAX_IMAGE_BYTES)
            {
                throw new InfrastructureException(MessageCode.AUTH_AVATAR_INVALID);
            }
            return await File.ReadAllBytesAsync(output, timeout.Token);
        }
        catch (MediaToolException)
        {
            throw new InfrastructureException(MessageCode.AUTH_AVATAR_INVALID);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InfrastructureException(MessageCode.REQUEST_TIMEOUT, 503);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
