namespace Yohuke.LiveRecorder.Server.Contracts;

/// <summary>显式为选定录播生成或更新播放清晰度。</summary>
public sealed record QueueTranscodeRequest(List<string> SessionIds, List<string>? QualityIds = null, bool Force = false);
