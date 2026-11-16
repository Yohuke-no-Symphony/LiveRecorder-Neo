using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Xunit;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Recording;
using Yohuke.LiveRecorder.Infrastructure.Transcoding;
using Yohuke.LiveRecorder.Recording.Contracts;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>验证录像节点分配明细的持久化归属、资料投影与管理员权限。</summary>
public sealed class RecordingWorkerAssignmentHttpTests
{
    /// <summary>停用且离线的节点仍可查看监控和停止中的房间，历史、孤立和其它节点的分配不会混入。</summary>
    [MongoHttpFact]
    public async Task ListsOnlyCurrentReservationsWithLatestStreamerMetadata()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        await fixture.App.Services.GetServices<IHostedService>().OfType<RecordingSupervisor>().Single().StopAsync(CancellationToken.None);
        var workers = fixture.App.Services.GetRequiredService<TranscodeWorkerService>();
        var ownership = fixture.App.Services.GetRequiredService<RecordingOwnershipService>();
        var worker = (await workers.CreateAsync("离线录像节点", "Recording", CancellationToken.None)).Worker;
        var target = new Streamer { RoomId = 123456, Name = "原昵称", AvatarUrl = "https://example.test/avatar.png", IsLive = false };
        var stopping = new Streamer { RoomId = 234567, Name = "停止中主播" };
        var released = new Streamer { RoomId = 345678, Name = "历史主播" };
        var other = new Streamer { RoomId = 456789, Name = "其它节点主播" };
        await fixture.Context.Collection<Streamer>("streamers").InsertManyAsync([target, stopping, released, other]);
        var currentAssignment = (await ownership.TryAcquireWorkerAsync(target, worker.Id, CancellationToken.None))!;
        var stoppingAssignment = (await ownership.TryAcquireWorkerAsync(stopping, worker.Id, CancellationToken.None))!;
        await ownership.RequestStopAsync(stoppingAssignment.Id, CancellationToken.None);
        var releasedAssignment = (await ownership.TryAcquireWorkerAsync(released, worker.Id, CancellationToken.None))!;
        await ownership.ReleaseWorkerAsync(releasedAssignment, CancellationToken.None);
        await ownership.TryAcquireWorkerAsync(other, "other-worker", CancellationToken.None);
        await fixture.Context.Collection<RecordingAssignmentRecord>("recordingAssignments").InsertOneAsync(new()
        {
            OwnerId = worker.Id, OwnerKind = "Worker", TargetId = "orphan", RoomId = 999999, Generation = 1
        });
        await fixture.Context.Collection<Streamer>("streamers").UpdateOneAsync(value => value.Id == target.Id,
            Builders<Streamer>.Update.Set(value => value.Name, "最新主播名称"));
        await fixture.Context.Collection<TranscodeWorker>("transcodeWorkers").UpdateOneAsync(value => value.Id == worker.Id,
            Builders<TranscodeWorker>.Update.Set(value => value.Enabled, false).Set(value => value.LastSeenAtUtc, DateTime.UtcNow.AddDays(-1)));

        using var response = await fixture.Admin.Client.GetAsync($"/api/transcoding/workers/{worker.Id}/assignments");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rooms = (await response.Content.ReadFromJsonAsync<JsonArray>())!;
        Assert.Equal(2, rooms.Count);
        var room = rooms.Single(value => value!["roomId"]!.GetValue<long>() == target.RoomId)!;
        Assert.Equal(target.Id, room["targetId"]!.GetValue<string>());
        Assert.Equal("最新主播名称", room["name"]!.GetValue<string>());
        Assert.Equal(new[] { "name", "roomId", "targetId" }, room.AsObject().Select(value => value.Key).Order().ToArray());
        Assert.Contains(rooms, value => value!["roomId"]!.GetValue<long>() == stopping.RoomId);
        Assert.Equal(currentAssignment.Id, (await ownership.GetAsync(target.RoomId))!.AssignmentId);
        Assert.True(response.Headers.CacheControl!.NoStore);
    }

    /// <summary>主播资料被移除后仍保留有效房间归属，名称回退到分配时保存的资料。</summary>
    [MongoHttpFact]
    public async Task MissingStreamerKeepsItsReservedRoomAndSavedName()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var worker = (await fixture.App.Services.GetRequiredService<TranscodeWorkerService>()
            .CreateAsync("录像节点", "Recording", CancellationToken.None)).Worker;
        var target = new Streamer { RoomId = 123456, Name = "已移除主播" };
        var assignment = (await fixture.App.Services.GetRequiredService<RecordingOwnershipService>()
            .TryAcquireWorkerAsync(target, worker.Id, CancellationToken.None))!;
        await fixture.Context.Collection<RecordingAssignmentRecord>("recordingAssignments").UpdateOneAsync(value => value.Id == assignment.Id,
            Builders<RecordingAssignmentRecord>.Update.Set(value => value.Definition, new RecordingAssignment { Name = target.Name }));

        var rooms = (await fixture.Admin.Client.GetFromJsonAsync<JsonArray>($"/api/transcoding/workers/{worker.Id}/assignments"))!;
        var room = Assert.Single(rooms)!;
        Assert.Equal(target.RoomId, room["roomId"]!.GetValue<long>());
        Assert.Equal(target.Name, room["name"]!.GetValue<string>());
    }

    /// <summary>仅管理员可读取，空节点返回空列表，不存在和其它类型的节点返回明确错误。</summary>
    [MongoHttpFact]
    public async Task RequiresAdministratorAndValidRecordingWorker()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        var workers = fixture.App.Services.GetRequiredService<TranscodeWorkerService>();
        var worker = (await workers.CreateAsync("录像节点", "Recording", CancellationToken.None)).Worker;
        var path = $"/api/transcoding/workers/{worker.Id}/assignments";
        using var anonymous = await fixture.Admin.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        var user = await fixture.CreateUserAsync("assignment-viewer@example.test");
        var viewer = fixture.NewBrowser();
        await viewer.LoginAsync(user.Email, HttpServerFixture.USER_PASSWORD);
        using var denied = await viewer.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        Assert.Empty((await fixture.Admin.Client.GetFromJsonAsync<JsonArray>(path))!);
        using var missing = await fixture.Admin.Client.GetAsync("/api/transcoding/workers/missing/assignments");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var transcoding = (await workers.CreateAsync("转码节点", CancellationToken.None)).Worker;
        using var wrongKind = await fixture.Admin.Client.GetAsync($"/api/transcoding/workers/{transcoding.Id}/assignments");
        Assert.Equal(HttpStatusCode.Conflict, wrongKind.StatusCode);
    }
}
