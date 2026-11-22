using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using MongoDB.Driver;
using Xunit;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>验证磁盘分类摘要、管理员目录明细和即时权限边界。</summary>
public sealed class StorageUsageHttpTests
{
    /// <summary>同一份真实目录统计支持成员摘要和仅含必要展示字段的管理员明细。</summary>
    [MongoHttpFact]
    public async Task AdministratorSeesRealDirectoryCategoriesAndProjectedRoomAndUserNames()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var owner = await fixture.CreateUserAsync("storage-owner@example.test");
        owner.DisplayName = "切片所有者";
        await fixture.Context.Collection<UserAccount>("users").UpdateOneAsync(user => user.Id == owner.Id,
            Builders<UserAccount>.Update.Set(user => user.DisplayName, owner.DisplayName));
        var streamer = new Streamer
        {
            RoomId = 31415,
            Uid = 27182,
            Name = "磁盘统计直播间",
            DirectoryName = "room_one",
            Enabled = false
        };
        await fixture.Context.Collection<Streamer>("streamers").InsertOneAsync(streamer);
        var sessionDirectory = Path.Combine(fixture.StoragePath, streamer.DirectoryName, "session");
        await WriteBytesAsync(Path.Combine(sessionDirectory, "source", "source.flv"), 11);
        await WriteBytesAsync(Path.Combine(sessionDirectory, "切片", owner.Id, "job", "v1", "output.mp4"), 13);
        var trash = new TrashEntry
        {
            OwnerId = owner.Id,
            Kind = "Clip",
            EntityId = Guid.NewGuid().ToString("N"),
            OriginalRelativePath = Path.Combine(streamer.DirectoryName, "session", "切片", owner.Id, "trashed-job", "v1", "output.mp4"),
            State = "Active",
            PurgeAtUtc = DateTime.UtcNow.AddDays(1)
        };
        trash.TrashRelativePath = Path.Combine(".trash", trash.Id, "payload.mp4");
        await WriteBytesAsync(Path.Combine(fixture.StoragePath, trash.TrashRelativePath), 17);
        await fixture.Context.Collection<TrashEntry>("trash").InsertOneAsync(trash);
        await WriteBytesAsync(Path.Combine(fixture.CachePath, "artwork", "avatar.image"), 19);
        var member = fixture.NewBrowser();
        await member.LoginAsync(owner.Email, HttpServerFixture.USER_PASSWORD);

        var summary = await member.Client.GetFromJsonAsync<JsonArray>("/api/storage");

        Assert.NotNull(summary);
        var recordings = Assert.Single(summary, item => item!["id"]!.GetValue<string>() == "recordings")!.AsObject();
        Assert.Equal(11L, Bytes(recordings, "recordingBytes"));
        Assert.Equal(13L, Bytes(recordings, "clipBytes"));
        Assert.Equal(17L, Bytes(recordings, "trashBytes"));
        Assert.Equal(0L, Bytes(recordings, "cacheBytes"));
        Assert.True(Bytes(recordings, "totalBytes") > 0);
        Assert.True(Bytes(recordings, "freeBytes") >= 0);
        Assert.True(Bytes(recordings, "otherBytes") >= 0);
        Assert.Equal(Bytes(recordings, "totalBytes"),
            Bytes(recordings, "recordingBytes") + Bytes(recordings, "clipBytes") + Bytes(recordings, "trashBytes")
            + Bytes(recordings, "otherBytes") + Bytes(recordings, "freeBytes"));
        var data = Assert.Single(summary, item => item!["id"]!.GetValue<string>() == "data")!.AsObject();
        Assert.Equal(19L, Bytes(data, "cacheBytes"));
        Assert.Equal(0L, Bytes(data, "recordingBytes"));
        Assert.Equal(0L, Bytes(data, "clipBytes"));
        Assert.Equal(0L, Bytes(data, "trashBytes"));
        Assert.DoesNotContain(owner.DisplayName, summary.ToJsonString());
        Assert.DoesNotContain(owner.Email, summary.ToJsonString());

        using var response = await fixture.Admin.Client.GetAsync("/api/storage/details");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var details = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.NotNull(details);
        Assert.True(DateTimeOffset.TryParse(details["scannedAtUtc"]!.GetValue<string>(), out _));
        Assert.Equal(summary.Count, details["volumes"]!.AsArray().Count);
        var room = Assert.Single(details["rooms"]!.AsArray(), item => item!["name"]!.GetValue<string>() == streamer.Name)!.AsObject();
        Assert.Equal(streamer.RoomId, room["roomId"]!.GetValue<long>());
        Assert.Equal(11L, Bytes(room, "recordingBytes"));
        Assert.Equal(13L, Bytes(room, "clipBytes"));
        Assert.Equal(17L, Bytes(room, "trashBytes"));
        Assert.Equal(41L, Bytes(room, "totalBytes"));
        var user = Assert.Single(details["users"]!.AsArray(), item => item!["ownerId"]!.GetValue<string>() == owner.Id)!.AsObject();
        Assert.Equal(owner.DisplayName, user["name"]!.GetValue<string>());
        Assert.Equal(13L, Bytes(user, "clipBytes"));
        Assert.Equal(17L, Bytes(user, "trashBytes"));
        Assert.Equal(30L, Bytes(user, "totalBytes"));
        var allowedUserFields = new HashSet<string>
        {
            "id", "name", "path", "ownerId", "recordingBytes", "clipBytes", "trashBytes", "otherBytes", "totalBytes"
        };
        Assert.All(user, property => Assert.Contains(property.Key, allowedUserFields));
        Assert.All<string>(["recordings", "clips", "trash", "cache"], kind =>
            Assert.Contains(details["directories"]!.AsArray(), item => item!["kind"]!.GetValue<string>() == kind));
        var serialized = details.ToJsonString();
        Assert.DoesNotContain(owner.PasswordHash, serialized);
        Assert.DoesNotContain(owner.Email, serialized);
        Assert.DoesNotContain("passwordHash", serialized);
        Assert.DoesNotContain("normalizedEmail", serialized);

        await WriteBytesAsync(Path.Combine(sessionDirectory, "source", "source.flv"), 23);
        var refreshed = await fixture.Admin.Client.GetFromJsonAsync<JsonObject>("/api/storage/details?refresh=true");
        var refreshedRoom = Assert.Single(refreshed!["rooms"]!.AsArray(), item => item!["roomId"]!.GetValue<long>() == streamer.RoomId)!.AsObject();
        Assert.Equal(23L, Bytes(refreshedRoom, "recordingBytes"));
        Assert.Equal(53L, Bytes(refreshedRoom, "totalBytes"));
        var refreshedSummary = await member.Client.GetFromJsonAsync<JsonArray>("/api/storage");
        var refreshedRecordings = Assert.Single(refreshedSummary!, item => item!["id"]!.GetValue<string>() == "recordings")!.AsObject();
        Assert.Equal(23L, Bytes(refreshedRecordings, "recordingBytes"));
    }

    /// <summary>磁盘统计将每个资产下的成品归到源直播间，未知归属的文件仍计入卷和目录。</summary>
    [MongoHttpFact]
    public async Task AttributesActualDerivedFilesToTheirSourceRooms()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var streamer = new Streamer
        {
            RoomId = 31415,
            Uid = 27182,
            Name = "转码统计直播间",
            DirectoryName = "room_one",
            Enabled = false
        };
        await fixture.Context.Collection<Streamer>("streamers").InsertOneAsync(streamer);
        var source = Path.Combine(streamer.DirectoryName, "session", "source.flv");
        var asset = new MediaAsset { SourceRelativePath = source };
        var secondAsset = new MediaAsset { SourceRelativePath = Path.Combine("room_two", "session", "source.flv") };
        await fixture.Context.Collection<MediaAsset>("mediaAssets").InsertManyAsync([asset, secondAsset]);
        await WriteBytesAsync(Path.Combine(fixture.StoragePath, source), 11);
        var derived = Path.Combine(fixture.StoragePath, ".derived");
        await WriteBytesAsync(Path.Combine(derived, asset.Id, "version", "recipe", "job-1", "init.mp4"), 13);
        await WriteBytesAsync(Path.Combine(derived, asset.Id, "version", "recipe", "job-1", "chunk_00000.m4s"), 17);
        await WriteBytesAsync(Path.Combine(derived, asset.Id, "version", "other-recipe", "other-job-1", "chunk_00000.m4s"), 19);
        await WriteBytesAsync(Path.Combine(derived, secondAsset.Id, "version", "recipe", "job-1", "chunk_00000.m4s"), 23);
        await WriteBytesAsync(Path.Combine(derived, "unknown-asset", "version", "recipe", "job-1", "chunk_00000.m4s"), 29);

        var summary = await fixture.Admin.Client.GetFromJsonAsync<JsonArray>("/api/storage");
        var recordings = Assert.Single(summary!, item => item!["id"]!.GetValue<string>() == "recordings")!.AsObject();
        Assert.Equal(101L, Bytes(recordings, "derivedBytes"));

        var details = await fixture.Admin.Client.GetFromJsonAsync<JsonObject>("/api/storage/details");
        var rooms = details!["rooms"]!.AsArray();
        Assert.Equal(2, rooms.Count);
        var room = Assert.Single(rooms, item => item!["name"]!.GetValue<string>() == streamer.Name)!.AsObject();
        Assert.Equal(streamer.RoomId, room["roomId"]!.GetValue<long>());
        Assert.Equal(11L, Bytes(room, "recordingBytes"));
        Assert.Equal(49L, Bytes(room, "derivedBytes"));
        Assert.Equal(60L, Bytes(room, "totalBytes"));
        var secondRoom = Assert.Single(rooms, item => item!["id"]!.GetValue<string>() == "room_two")!.AsObject();
        Assert.Equal(23L, Bytes(secondRoom, "derivedBytes"));
        Assert.Equal(23L, Bytes(secondRoom, "totalBytes"));
        var directory = Assert.Single(details["directories"]!.AsArray(), item => item!["kind"]!.GetValue<string>() == "derived")!.AsObject();
        Assert.Equal(101L, Bytes(directory, "derivedBytes"));
    }

    /// <summary>匿名请求不能读取磁盘摘要或明细，已登录普通成员仅能读取摘要。</summary>
    [MongoHttpFact]
    public async Task DirectoryDetailsRejectAnonymousAndOrdinaryMembers()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        var browser = fixture.NewBrowser();
        foreach (var endpoint in new[] { "/api/storage", "/api/storage/details" })
        {
            using var denied = await browser.Client.GetAsync(endpoint);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
        var user = await fixture.CreateUserAsync("storage-member@example.test");
        await browser.LoginAsync(user.Email, HttpServerFixture.USER_PASSWORD);
        using (var summary = await browser.Client.GetAsync("/api/storage"))
        {
            Assert.Equal(HttpStatusCode.OK, summary.StatusCode);
        }
        using (var denied = await browser.Client.GetAsync("/api/storage/details"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
    }

    /// <summary>管理员降权后保留成员磁盘摘要，已有 Cookie 立即失去目录明细权限。</summary>
    [MongoHttpFact]
    public async Task ExistingAdministratorCookieLosesDirectoryAccessOnTheNextRequest()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        var administrator = await fixture.CreateUserAsync("storage-admin@example.test", role: "Admin");
        var browser = fixture.NewBrowser();
        await browser.LoginAsync(administrator.Email, HttpServerFixture.USER_PASSWORD);
        using (var details = await browser.Client.GetAsync("/api/storage/details"))
        {
            Assert.Equal(HttpStatusCode.OK, details.StatusCode);
        }
        await fixture.Context.Collection<UserAccount>("users").UpdateOneAsync(account => account.Id == administrator.Id,
            Builders<UserAccount>.Update.Set(account => account.Role, "User"));

        using var denied = await browser.Client.GetAsync("/api/storage/details");
        using var summary = await browser.Client.GetAsync("/api/storage");

        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(HttpStatusCode.OK, summary.StatusCode);
    }

    private static long Bytes(JsonObject value, string property) => value[property]!.GetValue<long>();

    private static async Task WriteBytesAsync(string path, int length)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, new byte[length]);
    }
}
