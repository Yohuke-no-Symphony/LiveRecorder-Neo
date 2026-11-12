namespace Yohuke.LiveRecorder.Recording.Tests;

internal sealed record FlvReadChunk(int Offset, int Length, long TimestampMs);
