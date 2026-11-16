using System.Runtime.CompilerServices;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Library;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Library;

namespace Yohuke.LiveRecorder.Infrastructure.Files;

internal sealed class PlaybackEventWindowReader(EventArchiveReader reader)
{
    private const double PREFETCH_DURATION_MS = 30000;

    internal async Task<PlaybackEventWindow> ReadAsync(SessionDetail detail, string storageRoot, double offsetMs, string? kind,
        int limit, CancellationToken cancellationToken)
    {
        if (!double.IsFinite(offsetMs) || offsetMs < 0)
        {
            throw new InfrastructureException(MessageCode.LIBRARY_EVENT_RANGE_INVALID);
        }
        if (limit < 1)
        {
            throw new InfrastructureException(MessageCode.REQUEST_INVALID);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var fromMs = Math.Min(offsetMs, detail.DurationMs);
        var toMs = Math.Min(fromMs + PREFETCH_DURATION_MS, detail.DurationMs);
        var playbackEndMs = detail.Session.EndedAtUtc is null ? (double?)null : detail.DurationMs;
        var archives = new Dictionary<string, EventArchiveSnapshot>(StringComparer.Ordinal);
        try
        {
            var sources = await OpenSourcesAsync(detail, storageRoot, toMs, playbackEndMs, archives, cancellationToken);
            var selected = new PlaybackEventSelection(limit);
            var historyBlocks = sources.SelectMany(source => source.Archive.Blocks.Select(block => (Source: source, Block: block)))
                .Where(item => Intersects(item.Source, item.Block, 0, fromMs) && IncludesKind(item.Block, kind))
                .OrderByDescending(item => Math.Min(fromMs, item.Block.MaxMs + item.Source.Projection.StartOffsetMs))
                .ThenByDescending(item => checked(item.Source.SequencePrefix + item.Block.LastSequence));
            foreach (var item in historyBlocks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var maximumOffset = Math.Min(fromMs, item.Block.MaxMs + item.Source.Projection.StartOffsetMs);
                var maximumSequence = checked(item.Source.SequencePrefix + item.Block.LastSequence);
                if (selected.CanExclude(maximumOffset, maximumSequence))
                {
                    break;
                }
                await foreach (var message in ReadProjectedBlockAsync(item.Source, item.Block, fromMs, playbackEndMs, cancellationToken))
                {
                    if (PlaybackEventIdentity.IncludesKind(kind, message.Kind))
                    {
                        selected.Include(message);
                    }
                }
            }
            var items = selected.Items.ToDictionary(item => item.Sequence);
            var pocketKeys = selected.Items.Select(PlaybackEventIdentity.RedPocketKey).OfType<string>().ToHashSet(StringComparer.Ordinal);
            if (toMs > fromMs)
            {
                foreach (var source in sources)
                {
                    foreach (var block in source.Archive.Blocks)
                    {
                        if (!Intersects(source, block, fromMs, toMs) || !IncludesKind(block, kind))
                        {
                            continue;
                        }
                        await foreach (var message in ReadProjectedBlockAsync(source, block, toMs, playbackEndMs, cancellationToken))
                        {
                            if (message.OffsetMs <= fromMs || !PlaybackEventIdentity.IncludesKind(kind, message.Kind))
                            {
                                continue;
                            }
                            items[message.Sequence] = message;
                            if (PlaybackEventIdentity.RedPocketKey(message) is { } pocketKey)
                            {
                                pocketKeys.Add(pocketKey);
                            }
                        }
                    }
                }
            }
            if (pocketKeys.Count > 0)
            {
                foreach (var source in sources)
                {
                    foreach (var block in source.Archive.Blocks)
                    {
                        if (!Intersects(source, block, 0, fromMs) || !block.RedPocketKeys.Overlaps(pocketKeys))
                        {
                            continue;
                        }
                        await foreach (var message in ReadProjectedBlockAsync(source, block, fromMs, playbackEndMs, cancellationToken))
                        {
                            if (PlaybackEventIdentity.RedPocketKey(message) is { } pocketKey && pocketKeys.Contains(pocketKey))
                            {
                                items[message.Sequence] = message;
                            }
                        }
                    }
                }
            }
            return new()
            {
                FromMs = fromMs,
                ToMs = toMs,
                Items = items.Values.OrderBy(item => item.OffsetMs).ThenBy(item => item.Sequence).ToList()
            };
        }
        finally
        {
            foreach (var archive in archives.Values)
            {
                await archive.DisposeAsync();
            }
        }
    }

    private async Task<List<PlaybackEventSource>> OpenSourcesAsync(SessionDetail detail, string storageRoot, double toMs,
        double? playbackEndMs, Dictionary<string, EventArchiveSnapshot> archives, CancellationToken cancellationToken)
    {
        var sources = SessionGroupingService.Sources(detail.Session, playbackEndMs);
        var opened = new List<PlaybackEventSource>();
        for (var index = 0; index < sources.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = sources[index];
            if (source.FromMs + source.StartOffsetMs > toMs ||
                source.ToMs is { } end && (end <= source.FromMs || end + source.StartOffsetMs <= 0))
            {
                continue;
            }
            var directory = Path.Combine(MediaPaths.Resolve(storageRoot, source.DirectoryPath), "弹幕");
            if (!archives.TryGetValue(directory, out var archive))
            {
                archive = await reader.OpenSnapshotAsync(directory, cancellationToken);
                if (archive is null)
                {
                    continue;
                }
                archives.Add(directory, archive);
            }
            var prefix = detail.Session.EventSources.Count == 0 ? 0 : (index + 1) * SessionEventReader.SOURCE_SEQUENCE_STRIDE;
            opened.Add(new(archive, source, prefix));
        }
        return opened;
    }

    private static bool IncludesKind(EventIndexBlock block, string? kind)
    {
        return block.Kinds.Any(eventKind => PlaybackEventIdentity.IncludesKind(kind, eventKind));
    }

    private static bool Intersects(PlaybackEventSource source, EventIndexBlock block, double fromMs, double toMs)
    {
        return block.MaxMs >= source.Projection.FromMs &&
               (source.Projection.ToMs is null || block.MinMs < source.Projection.ToMs) &&
               block.MaxMs + source.Projection.StartOffsetMs >= fromMs &&
               block.MinMs + source.Projection.StartOffsetMs <= toMs;
    }

    private static async IAsyncEnumerable<RecordedEventEnvelope> ReadProjectedBlockAsync(PlaybackEventSource source,
        EventIndexBlock block, double maximumOffsetMs, double? playbackEndMs, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in EventArchiveReader.ReadBlockAsync(source.Archive, block, cancellationToken))
        {
            if (item.OffsetMs < source.Projection.FromMs || source.Projection.ToMs is { } end && item.OffsetMs >= end)
            {
                continue;
            }
            var offset = item.OffsetMs + source.Projection.StartOffsetMs;
            if (offset < 0 || offset > maximumOffsetMs || playbackEndMs is { } playbackEnd && offset >= playbackEnd)
            {
                continue;
            }
            item.OffsetMs = offset;
            item.Sequence = checked(source.SequencePrefix + item.Sequence);
            item.RawJson = "";
            yield return item;
        }
    }
}
