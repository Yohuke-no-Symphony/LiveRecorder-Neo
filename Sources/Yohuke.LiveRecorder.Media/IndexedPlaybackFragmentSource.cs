using System.Globalization;
using System.Text;
using Yohuke.LiveRecorder.Application.Media;

namespace Yohuke.LiveRecorder.Media;

internal sealed class IndexedPlaybackFragmentSource : IPlaybackFragmentSource
{
    private readonly IndexedPlaybackWindowBuilder builder;
    private readonly string sourcePath;
    private readonly long sourceLength;
    private readonly DateTime modifiedAt;
    private readonly IndexedPlaybackRange[] ranges;

    public long CommittedBytes { get; }
    public IReadOnlyList<PlaybackFragment> Fragments { get; }

    internal IndexedPlaybackFragmentSource(IndexedPlaybackWindowBuilder builder, string sourcePath, long sourceLength,
        DateTime modifiedAt, long committedBytes, IndexedPlaybackRange[] ranges)
    {
        this.builder = builder;
        this.sourcePath = sourcePath;
        this.sourceLength = sourceLength;
        this.modifiedAt = modifiedAt;
        this.ranges = ranges;
        CommittedBytes = committedBytes;
        Fragments = Array.AsReadOnly(ranges.Select((range, sequence) => new PlaybackFragment
        {
            Sequence = sequence,
            SourceStartMs = range.First.TimestampMs,
            SourceEndMs = range.EndMs
        }).ToArray());
    }

    /// <inheritdoc />
    public string CreatePlaylist(Func<int, string, string> assetUri)
    {
        ArgumentNullException.ThrowIfNull(assetUri);
        var targetDuration = Math.Ceiling(Fragments.Max(fragment => fragment.DurationMs) / 1000);
        var playlist = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-PLAYLIST-TYPE:VOD\n#EXT-X-INDEPENDENT-SEGMENTS\n#EXT-X-MEDIA-SEQUENCE:0\n");
        playlist.Append("#EXT-X-TARGETDURATION:").Append(targetDuration.ToString(CultureInfo.InvariantCulture)).Append('\n');
        foreach (var fragment in Fragments)
        {
            var initializationUri = ValidateUri(assetUri(fragment.Sequence, "init.mp4"));
            var mediaUri = ValidateUri(assetUri(fragment.Sequence, "chunk_00000.m4s"));
            playlist.Append("#EXT-X-MAP:URI=\"").Append(initializationUri).Append("\"\n");
            playlist.Append("#EXTINF:").Append((fragment.DurationMs / 1000).ToString("0.######", CultureInfo.InvariantCulture)).Append(",\n");
            playlist.Append(mediaUri).Append('\n');
        }
        return playlist.Append("#EXT-X-ENDLIST\n").ToString();
    }

    /// <inheritdoc />
    public Task<PlaybackWindowResult> BuildFragmentAsync(int sequence, string outputDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(sequence, ranges.Length);
        ValidateSource();
        return builder.BuildRangeAsync(sourcePath, outputDirectory, false, ranges[sequence], true, null, cancellationToken, ValidateSource);
    }

    private void ValidateSource()
    {
        var file = new FileInfo(sourcePath);
        if (file.Length != sourceLength || file.LastWriteTimeUtc != modifiedAt)
        {
            throw new IOException("The recording changed after its continuous playback plan was created.");
        }
    }

    private static string ValidateUri(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(character => char.IsControl(character) || character == '"')
            || value.StartsWith('#'))
        {
            throw new ArgumentException("A playback asset URI cannot be empty or contain playlist delimiters.", nameof(value));
        }
        return value;
    }
}
