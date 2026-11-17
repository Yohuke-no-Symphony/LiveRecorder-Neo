using Microsoft.Extensions.Options;
using Yohuke.LiveRecorder.Application.Media;

namespace Yohuke.LiveRecorder.Media;

/// <summary>一次解析已关闭的索引，为连续播放规划互不重叠的关键帧范围。</summary>
public sealed class IndexedPlaybackFragmentSourceFactory(IndexedPlaybackWindowBuilder builder, IOptions<MediaToolOptions> options) : IPlaybackFragmentSourceFactory
{
    public string CacheVersion => FormattableString.Invariant($"avc-aac-continuous-v1:{options.Value.HlsSegmentDuration.Ticks}");

    /// <inheritdoc />
    public async Task<IPlaybackFragmentSource> OpenAsync(PlaybackFragmentSourceRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!request.IsClosed)
        {
            throw new NotSupportedException("Continuous VOD requires a closed recording segment.");
        }
        var targetDurationMs = options.Value.HlsSegmentDuration.TotalMilliseconds;
        if (!double.IsFinite(targetDurationMs) || targetDurationMs <= 0)
        {
            throw new InvalidOperationException("The playback fragment duration must be finite and positive.");
        }
        var file = new FileInfo(Path.GetFullPath(request.SourcePath));
        var sourceLength = file.Length;
        var modifiedAt = file.LastWriteTimeUtc;
        var committed = request.CommittedBytes > 0 ? request.CommittedBytes : sourceLength;
        if (committed < 13 || committed > sourceLength)
        {
            throw new InvalidDataException("The closed recording does not contain its committed bytes.");
        }
        var index = await FlvIndexSnapshot.ReadAsync(request.IndexPath, committed, cancellationToken);
        var finalCommit = index.Entries.LastOrDefault(entry => entry.Kind == "Committed");
        if (finalCommit is null || finalCommit.Offset != committed)
        {
            throw new InvalidDataException("The closed recording index does not cover its final committed boundary.");
        }
        var durationMs = finalCommit.TimestampMs;
        if (!double.IsFinite(durationMs) || durationMs <= 0)
        {
            throw new InvalidDataException("The recording index has no finite committed duration.");
        }
        var ranges = new List<IndexedPlaybackRange>();
        byte[]? videoHeader = null;
        byte[]? audioHeader = null;
        FlvIndexEntry? first = null;
        FlvIndexEntry? previous = null;
        double? timestampOriginMs = null;
        foreach (var entry in index.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Kind == "VideoHeader")
            {
                if (entry.HeaderData is not { Length: > 0 } header || (header[0] & 15) != 7
                    || (videoHeader is not null && !videoHeader.AsSpan().SequenceEqual(header)))
                {
                    throw new NotSupportedException("Continuous playback requires a stable AVC decoder configuration.");
                }
                videoHeader ??= header;
            }
            else if (entry.Kind == "AudioHeader")
            {
                if (entry.HeaderData is not { Length: > 0 } header || header[0] >> 4 != 10
                    || (audioHeader is not null && !audioHeader.AsSpan().SequenceEqual(header))
                    || (first is not null && audioHeader is null))
                {
                    throw new NotSupportedException("Continuous playback requires a stable AAC decoder configuration.");
                }
                audioHeader ??= header;
            }
            else if (entry.Kind == "Keyframe")
            {
                if (videoHeader is null || !double.IsFinite(entry.TimestampMs) || entry.TimestampMs < 0
                    || entry.TimestampMs >= durationMs
                    || (previous is not null && (entry.Offset <= previous.Offset || entry.TimestampMs <= previous.TimestampMs)))
                {
                    throw new InvalidDataException("The recording must have ordered keyframes with decoder configuration.");
                }
                first ??= entry;
                timestampOriginMs ??= entry.TimestampMs;
                if (entry.TimestampMs - first.TimestampMs >= targetDurationMs)
                {
                    ranges.Add(new()
                    {
                        First = first,
                        EndOffset = entry.Offset,
                        EndMs = entry.TimestampMs,
                        TimestampOriginMs = timestampOriginMs.Value,
                        VideoHeader = videoHeader,
                        AudioHeader = audioHeader
                    });
                    first = entry;
                }
                previous = entry;
            }
        }
        if (first is null || videoHeader is null)
        {
            throw new InvalidDataException("The recording has no committed keyframe.");
        }
        ranges.Add(new()
        {
            First = first,
            EndOffset = committed,
            EndMs = durationMs,
            TimestampOriginMs = timestampOriginMs!.Value,
            VideoHeader = videoHeader,
            AudioHeader = audioHeader,
            ReachesCommittedEnd = true
        });
        file.Refresh();
        if (file.Length != sourceLength || file.LastWriteTimeUtc != modifiedAt)
        {
            throw new IOException("The recording changed while its playback index was being read.");
        }
        return new IndexedPlaybackFragmentSource(builder, file.FullName, sourceLength, modifiedAt, committed, ranges.ToArray());
    }
}
