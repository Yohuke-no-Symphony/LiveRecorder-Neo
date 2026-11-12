namespace Yohuke.LiveRecorder.Application.Bilibili;

/// <summary>带实际清晰度与采集请求头的临时直播地址。</summary>
public sealed record LiveStreamDescriptor(string Url, IReadOnlyDictionary<string, string> Headers, int Quality, string Codec, string Format);
