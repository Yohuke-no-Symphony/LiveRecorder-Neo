using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Library;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Library;

namespace Yohuke.LiveRecorder.Infrastructure.Files;

/// <summary>从已提交归档生成可验证的浏览器快照，只在服务器保留有界的长游标元数据。</summary>
public sealed class SessionEventSnapshotService(EventArchiveReader reader) : IDisposable
{
    private const int CURRENT_SCHEMA_VERSION = 2;
    private const int BUFFER_BYTES = 65536;
    private const int MAX_CURSOR_CHARACTERS = 4096;
    private const long CURSOR_CACHE_BYTES = 4 * 1024 * 1024;
    private static string EmptyHash { get; } = Convert.ToHexString(SHA256.HashData(ReadOnlySpan<byte>.Empty));
    private readonly MemoryCache cursors = new(new MemoryCacheOptions { SizeLimit = CURSOR_CACHE_BYTES });

    /// <summary>验证每个来源的完整已提交摘要与旧前缀，合法追加只返回新记录，其他变更重置快照。</summary>
    public async Task<SessionEventSnapshotResult> ReadAsync(SessionDetail detail, string storageRoot, string translationPattern,
        string? cursor = null, string? ifNoneMatch = null, CancellationToken cancellationToken = default)
    {
        if (detail.Session.IsHidden || detail.Session.IsTrashed || !detail.Session.ProjectionReady)
        {
            throw new InfrastructureException(MessageCode.LIBRARY_SESSION_NOT_FOUND, 404);
        }
        if (!double.IsFinite(detail.DurationMs) || detail.DurationMs < 0)
        {
            throw new InfrastructureException(MessageCode.LIBRARY_EVENT_RANGE_INVALID);
        }
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await ReadCoreAsync(detail, storageRoot, translationPattern, cursor, ifNoneMatch, cancellationToken);
            }
            catch (SessionEventSnapshotChangedException) when (attempt < 2)
            {
            }
            catch (SessionEventSnapshotChangedException)
            {
                throw new InfrastructureException(MessageCode.SESSION_CONFIGURATION_CHANGED, 409);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        cursors.Dispose();
    }

    private async Task<SessionEventSnapshotResult> ReadCoreAsync(SessionDetail detail, string storageRoot, string translationPattern,
        string? cursor, string? ifNoneMatch, CancellationToken cancellationToken)
    {
        Regex? pattern = null;
        string? patternError = null;
        try
        {
            pattern = TranslationDanmuPattern.Create(translationPattern);
        }
        catch (InfrastructureException error) when (error.Code == nameof(MessageCode.TRANSLATION_DANMU_PATTERN_INVALID))
        {
            patternError = error.Code;
        }
        var isLive = detail.Session.EndedAtUtc is null;
        var projections = SessionGroupingService.Sources(detail.Session, isLive ? null : detail.DurationMs);
        var contextHash = Hash(JsonSerializer.SerializeToUtf8Bytes(new
        {
            SchemaVersion = CURRENT_SCHEMA_VERSION,
            detail.Session.Id,
            detail.Session.StartedAtUtc,
            detail.Session.MediaRevision,
            TranslationPattern = translationPattern,
            Language = CultureInfo.CurrentUICulture.Name,
            IsLive = isLive,
            PlaybackEndMs = isLive ? (double?)null : detail.DurationMs,
            Projected = detail.Session.EventSources.Count > 0,
            Sources = projections.Select(source => new
            {
                Directory = Path.GetFullPath(MediaPaths.Resolve(storageRoot, source.DirectoryPath)),
                source.StartOffsetMs,
                source.FromMs,
                source.ToMs
            })
        }, JsonFiles.Options));
        var previous = DecodeCursor(cursor);
        var canResume = previous is not null && previous.ContextHash == contextHash && previous.Sources.Count == projections.Count;
        var sources = new List<SessionEventSnapshotSource>(projections.Count);
        for (var index = 0; index < projections.Count; index++)
        {
            var projection = projections[index];
            var directory = Path.Combine(MediaPaths.Resolve(storageRoot, projection.DirectoryPath), "弹幕");
            await using var archive = await reader.OpenSnapshotAsync(directory, cancellationToken);
            var length = archive?.CommittedLength ?? 0;
            var baseline = canResume ? previous!.Sources[index] : null;
            var (hash, prefixHash) = archive is null
                ? (EmptyHash, baseline?.Length == 0 ? EmptyHash : null)
                : await HashAsync(archive, length, baseline?.Length, cancellationToken);
            canResume &= baseline is not null && baseline.Length <= length && baseline.Hash == prefixHash;
            var prefix = detail.Session.EventSources.Count == 0 ? 0 : checked((index + 1) * SessionEventReader.SOURCE_SEQUENCE_STRIDE);
            sources.Add(new(directory, projection, prefix, new(length, hash)));
        }
        var positions = sources.Select(source => source.Position).ToArray();
        var identity = Hash(JsonSerializer.SerializeToUtf8Bytes(new { Context = contextHash, Sources = positions }, JsonFiles.Options));
        var translationError = canResume ? previous!.TranslationErrorCode ?? patternError : patternError;
        var entityTag = EntityTag(identity, translationError);
        var unchanged = canResume && positions.Select((position, index) => position.Length == previous!.Sources[index].Length).All(value => value);
        if (unchanged && Matches(ifNoneMatch, entityTag))
        {
            return new() { EntityTag = entityTag, NotModified = true };
        }
        var version = canResume ? previous!.Version : identity;
        var items = new List<RecordedEventEnvelope>();
        var translationSequences = new List<long>();
        for (var index = 0; index < sources.Count; index++)
        {
            var source = sources[index];
            var startByte = canResume ? previous!.Sources[index].Length : 0;
            if (startByte == source.Position.Length)
            {
                continue;
            }
            await using var archive = await reader.OpenSnapshotAsync(source.Directory, cancellationToken);
            if (archive is null)
            {
                if (source.Position.Length != 0)
                {
                    throw new SessionEventSnapshotChangedException();
                }
                continue;
            }
            if (archive.CommittedLength < source.Position.Length)
            {
                throw new SessionEventSnapshotChangedException();
            }
            await foreach (var item in EventArchiveReader.ReadSnapshotAsync(archive, startByte, source.Position.Length, cancellationToken))
            {
                if (!PlaybackEventIdentity.IncludesKind(null, item.Kind) || item.OffsetMs < source.Projection.FromMs ||
                    source.Projection.ToMs is { } end && item.OffsetMs >= end)
                {
                    continue;
                }
                var offsetMs = item.OffsetMs + source.Projection.StartOffsetMs;
                if (offsetMs < 0 || !double.IsFinite(offsetMs) || !isLive && offsetMs >= detail.DurationMs)
                {
                    continue;
                }
                item.OffsetMs = offsetMs;
                item.Sequence = checked(source.SequencePrefix + item.Sequence);
                item.RawJson = "";
                items.Add(item);
                if (item.Kind == "Danmu" && translationError is null && pattern is not null)
                {
                    try
                    {
                        if (pattern.IsMatch(item.Text))
                        {
                            translationSequences.Add(item.Sequence);
                        }
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        translationError = nameof(MessageCode.TRANSLATION_DANMU_PATTERN_TIMEOUT);
                        translationSequences.Clear();
                    }
                }
            }
            var (verifiedHash, _) = await HashAsync(archive, source.Position.Length, null, cancellationToken);
            if (verifiedHash != source.Position.Hash)
            {
                throw new SessionEventSnapshotChangedException();
            }
        }
        items.Sort((left, right) =>
        {
            var offset = left.OffsetMs.CompareTo(right.OffsetMs);
            return offset != 0 ? offset : left.Sequence.CompareTo(right.Sequence);
        });
        entityTag = EntityTag(identity, translationError);
        if ((cursor is null || canResume) && Matches(ifNoneMatch, entityTag))
        {
            return new() { EntityTag = entityTag, NotModified = true };
        }
        var response = new SessionEventSnapshot
        {
            Version = version,
            Reset = !canResume,
            IsLive = isLive,
            Items = items,
            TranslationSequences = translationSequences,
            TranslationError = translationError,
            Cursor = EncodeCursor(new(CURRENT_SCHEMA_VERSION, version, contextHash, positions, translationError))
        };
        return new() { EntityTag = entityTag, Snapshot = response };
    }

    private SessionEventSnapshotCursor? DecodeCursor(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MAX_CURSOR_CHARACTERS)
        {
            return null;
        }
        if (value.StartsWith("r.", StringComparison.Ordinal))
        {
            return cursors.TryGetValue(value, out SessionEventSnapshotCursor? saved) ? saved : null;
        }
        try
        {
            var encoded = value.Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + (4 - encoded.Length % 4) % 4, '=');
            var result = JsonSerializer.Deserialize<SessionEventSnapshotCursor>(Convert.FromBase64String(encoded), JsonFiles.Options);
            return result is not null && result.SchemaVersion == CURRENT_SCHEMA_VERSION && ValidHash(result.Version) && ValidHash(result.ContextHash) &&
                   (result.TranslationErrorCode is null or nameof(MessageCode.TRANSLATION_DANMU_PATTERN_INVALID) or nameof(MessageCode.TRANSLATION_DANMU_PATTERN_TIMEOUT)) &&
                   result.Sources is not null && result.Sources.All(source => source is not null && source.Length >= 0 && ValidHash(source.Hash))
                ? result
                : null;
        }
        catch (Exception error) when (error is JsonException or FormatException)
        {
            return null;
        }
    }

    private string EncodeCursor(SessionEventSnapshotCursor value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonFiles.Options);
        var encoded = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        if (encoded.Length <= MAX_CURSOR_CHARACTERS)
        {
            return encoded;
        }
        var key = "r." + Hash(bytes);
        if (bytes.Length <= CURSOR_CACHE_BYTES)
        {
            cursors.Set(key, value, new MemoryCacheEntryOptions { Size = bytes.Length, SlidingExpiration = TimeSpan.FromMinutes(30) });
        }
        return key;
    }

    private static bool ValidHash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static string EntityTag(string identity, string? translationError) =>
        "W/\"" + Hash(JsonSerializer.SerializeToUtf8Bytes(new { Identity = identity, TranslationError = translationError }, JsonFiles.Options)) + "\"";

    private static bool Matches(string? requested, string entityTag) => requested?.Split(',').Any(value =>
        value.Trim() is var tag && (tag == "*" || NormalizeTag(tag) == NormalizeTag(entityTag))) == true;

    private static string NormalizeTag(string tag) => tag.StartsWith("W/", StringComparison.Ordinal) ? tag[2..] : tag;

    private static async Task<(string Hash, string? PrefixHash)> HashAsync(EventArchiveSnapshot archive, long length,
        long? prefixLength, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var prefix = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BUFFER_BYTES);
        try
        {
            var position = 0L;
            while (position < length)
            {
                var read = await RandomAccess.ReadAsync(archive.Stream.SafeFileHandle,
                    buffer.AsMemory(0, (int)Math.Min(BUFFER_BYTES, length - position)), position, cancellationToken);
                if (read == 0)
                {
                    throw new SessionEventSnapshotChangedException();
                }
                hash.AppendData(buffer, 0, read);
                if (prefixLength is { } boundary && position < boundary)
                {
                    prefix.AppendData(buffer, 0, (int)Math.Min(read, boundary - position));
                }
                position += read;
            }
            return (Convert.ToHexString(hash.GetHashAndReset()), prefixLength <= length ? Convert.ToHexString(prefix.GetHashAndReset()) : null);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
