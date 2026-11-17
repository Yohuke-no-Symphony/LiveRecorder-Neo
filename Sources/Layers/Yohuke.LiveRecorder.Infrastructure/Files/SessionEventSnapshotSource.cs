using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Infrastructure.Files;

internal sealed record SessionEventSnapshotSource(string Directory, EventArchiveSource Projection, long SequencePrefix,
    SessionEventSnapshotPosition Position);
