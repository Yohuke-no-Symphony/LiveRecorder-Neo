using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Xunit;
using Yohuke.LiveRecorder.Application.Tasks;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Recording;
using Yohuke.LiveRecorder.Recording.Contracts;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>验证独立采集任务的播放链接保持逻辑场次身份。</summary>
public sealed class RecordingLogicalTaskHistoryTests
{
    /// <summary>物理采集任务身份不变，播放目标指向合并后的稳定场次。</summary>
    [MongoHttpFact]
    public async Task CaptureTaskLinksToItsLogicalBroadcast()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        await fixture.App.Services.GetServices<IHostedService>().OfType<RecordingSupervisor>().Single().StopAsync(CancellationToken.None);
        var started = DateTime.UtcNow.AddMinutes(-1);
        await fixture.Context.Collection<BroadcastSession>("sessions").InsertOneAsync(new()
        {
            Id = "logical-broadcast", RoomId = 123, StreamerId = "streamer", Title = "Same broadcast",
            StartedAtUtc = started, LastMediaAtUtc = started.AddSeconds(5), DirectoryPath = "logical-broadcast"
        });
        await fixture.Context.Collection<RecordingSessionTaskSnapshot>("recordingSessionTasks").InsertOneAsync(new()
        {
            Id = "physical-capture", WorkerId = "recorder", AssignmentId = "assignment", Generation = 1,
            CaptureState = "Recording", UploadState = "CaughtUp", LastReportedAtUtc = DateTime.UtcNow,
            Descriptor = new RecordingSessionDescriptor
            {
                SessionId = "physical-capture", LogicalSessionId = "logical-broadcast", LogicalStartedAtUtc = started,
                RoomId = 123, Title = "Same broadcast", StartedAtUtc = started.AddSeconds(2)
            }
        });

        var page = (await fixture.Admin.Client.GetFromJsonAsync<TaskHistoryPage>("/api/admin/worker-tasks?kind=recording"))!;

        var task = Assert.Single(page.Items, value => value.Id == "physical-capture");
        Assert.Equal("logical-broadcast", task.SessionId);
        Assert.Equal("recorder", task.WorkerId);
    }
}
