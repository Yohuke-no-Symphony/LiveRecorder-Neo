namespace Yohuke.LiveRecorder.Infrastructure.Files;

internal sealed record EventArchiveIndexCacheEntry(
    EventArchiveFileVersion EventsVersion,
    long CommittedLength,
    string PrefixHash,
    string TailHash,
    EventArchiveFileVersion? IndexVersion,
    string IndexHash,
    IReadOnlyList<EventIndexBlock> Blocks);
