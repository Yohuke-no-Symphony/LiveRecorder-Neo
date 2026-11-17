namespace Yohuke.LiveRecorder.Media.Tests;

internal sealed record SeamlessMediaPacket(string StreamKind, string DataHash, decimal DtsMs, decimal PtsMs)
{
    internal SeamlessMediaPacket AtOffset(decimal offsetMs) => this with { DtsMs = DtsMs + offsetMs, PtsMs = PtsMs + offsetMs };
}
