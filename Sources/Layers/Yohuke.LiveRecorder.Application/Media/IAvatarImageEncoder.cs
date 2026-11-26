namespace Yohuke.LiveRecorder.Application.Media;

/// <summary>将裁剪后的图像重编码为有大小上限的站内头像。</summary>
public interface IAvatarImageEncoder
{
    const int MAX_IMAGE_BYTES = 128 * 1024;

    /// <summary>校验上传内容并输出单帧 256 × 256 JPEG。</summary>
    Task<byte[]> EncodeAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken);
}
