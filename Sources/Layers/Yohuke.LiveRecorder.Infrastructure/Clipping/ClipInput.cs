namespace Yohuke.LiveRecorder.Infrastructure.Clipping;

/// <summary>Authorizes only the assigned bytes of a physical clipping source.</summary>
public sealed record ClipInput(string Path, string ETag, long SourceLength, long StartOffset, long EndOffset);
