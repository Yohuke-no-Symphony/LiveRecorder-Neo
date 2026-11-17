using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Infrastructure.Files;

internal sealed record PlaybackEventSource(EventArchiveSnapshot Archive, EventArchiveSource Projection, long SequencePrefix);
