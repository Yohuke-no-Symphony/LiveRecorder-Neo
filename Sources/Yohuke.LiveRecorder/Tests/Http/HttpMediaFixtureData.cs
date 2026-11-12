using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Playback;

namespace Yohuke.LiveRecorder.Http.Tests;

internal sealed record HttpMediaFixtureData(BroadcastSession Session, Clip Clip, MediaJob Job, string PlaybackUrl, byte[] ClipBytes, byte[] ImageBytes)
{
    internal static async Task<HttpMediaFixtureData> CreateAsync(HttpServerFixture fixture, string ownerId)
    {
        var now = DateTime.UtcNow;
        var streamer = new Streamer { Uid = 42, RoomId = 100, Name = "HTTP 测试主播", Enabled = false };
        var session = new BroadcastSession { StreamerId = streamer.Id, RoomId = 100, Title = "测试直播", DirectoryPath = Path.Combine("测试主播_42", Guid.NewGuid().ToString("N")), StartedAtUtc = now.AddMinutes(-1), EndedAtUtc = now.AddMinutes(-1).AddSeconds(10), LastMediaAtUtc = now.AddMinutes(-1).AddSeconds(10), State = "Completed", HasDanmu = true, CoverUrl = "https://i0.hdslb.com/http-test-" + Guid.NewGuid().ToString("N") + ".png" };
        var segment = new RecordingSegment { SessionId = session.Id, RelativePath = Path.Combine("录像", "录像_001.flv"), StartOffsetMs = 0, DurationMs = 10000, CommittedBytes = 64, IsClosed = true };
        await fixture.Context.Collection<Streamer>("streamers").InsertOneAsync(streamer);
        await fixture.Context.Collection<BroadcastSession>("sessions").InsertOneAsync(session);
        await fixture.Context.Collection<RecordingSegment>("segments").InsertOneAsync(segment);
        var directory = Path.Combine(fixture.StoragePath, session.DirectoryPath);
        var eventsDirectory = Path.Combine(directory, "弹幕");
        Directory.CreateDirectory(eventsDirectory);
        var recorded = new RecordedEventEnvelope { Sequence = 1, EventId = "http-event", Kind = "Danmu", Command = "DANMU_MSG", OffsetMs = 500, ReceivedAtUtc = now, SourceTimeUtc = now, UserId = ownerId, UserName = "观众", Text = "测试弹幕", RawJson = "{\"cmd\":\"DANMU_MSG\"}" };
        await File.WriteAllTextAsync(Path.Combine(eventsDirectory, "事件.jsonl"), JsonSerializer.Serialize(recorded, JsonFiles.Options) + "\n");
        var image = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl6hAAAAABJRU5ErkJggg==");
        var artwork = Path.Combine(fixture.StoragePath, ".cache", "artwork", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(session.CoverUrl))).ToLowerInvariant() + ".image");
        Directory.CreateDirectory(Path.GetDirectoryName(artwork)!);
        await File.WriteAllBytesAsync(artwork, image);
        await File.WriteAllTextAsync(artwork + ".type", "image/png");
        var clip = new Clip { OwnerId = ownerId, SessionId = session.Id, Title = "测试切片", ExpiresAtUtc = now.AddHours(1) };
        clip.JobId = clip.Id;
        clip.RelativePath = Path.Combine(session.DirectoryPath, "切片", ownerId, clip.Id, "片段.mp4");
        var clipPath = Path.Combine(fixture.StoragePath, clip.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(clipPath)!);
        var bytes = Enumerable.Range(0, 64).Select(x => (byte)x).ToArray();
        await File.WriteAllBytesAsync(clipPath, bytes);
        clip.Bytes = bytes.Length;
        var job = new MediaJob { Id = clip.JobId, SessionId = session.Id, OwnerId = ownerId, Kind = "Clip", State = "Completed", Progress = 100, Title = clip.Title, OutputRelativePath = clip.RelativePath, OutputRelativePaths = [clip.RelativePath], FinishedAtUtc = now };
        await fixture.Context.Collection<Clip>("clips").InsertOneAsync(clip);
        await fixture.Context.Collection<MediaJob>("jobs").InsertOneAsync(job);
        var key = Guid.NewGuid().ToString("N");
        var generation = Guid.NewGuid().ToString("N");
        var cache = Path.Combine(fixture.StoragePath, ".cache", "playback", key, generation);
        await JsonFiles.WriteAsync(Path.Combine(cache, "window.json"), new PlaybackCacheEntry { SessionId = session.Id, SegmentId = segment.Id, Generation = generation, SourceBytes = 64, StartOffsetMs = 0, EndOffsetMs = 10000, BuiltAtUtc = now });
        await File.WriteAllTextAsync(Path.Combine(cache, "index.m3u8"), "#EXTM3U\n#EXT-X-VERSION:3\n#EXTINF:10,\nsegment0.ts\n#EXT-X-ENDLIST\n");
        await File.WriteAllBytesAsync(Path.Combine(cache, "segment0.ts"), bytes);
        return new(session, clip, job, $"/api/playback/{key}/{generation}/index.m3u8", bytes, image);
    }
}
