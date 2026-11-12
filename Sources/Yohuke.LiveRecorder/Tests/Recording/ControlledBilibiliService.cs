using System.Collections.Concurrent;
using Yohuke.LiveRecorder.Application.Bilibili;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Recording.Tests;

internal sealed class ControlledBilibiliService : IBilibiliService
{
    internal ConcurrentDictionary<long, ControlledRoom> Rooms { get; } = new();

    public async Task<LiveRoomSnapshot> GetRoomAsync(long roomId, string? accountId = null, CancellationToken cancellationToken = default)
    {
        var room = Rooms[roomId];
        await room.BeforeQueryAsync(cancellationToken);
        if (room.QueryFailure is { } error)
        {
            throw error;
        }
        return new(roomId, roomId + 100000, $"测试主播 {roomId}", "", room.Title, "", room.IsLive, room.LiveStartedAtUtc);
    }

    public Task<LiveStreamDescriptor> ResolveStreamAsync(long roomId, int quality, string? accountId = null, CancellationToken cancellationToken = default) => Task.FromResult(new LiveStreamDescriptor($"https://recording-fixture.invalid/{roomId}.flv", new Dictionary<string, string>(), quality, "avc", "flv"));

    public async Task SubscribeEventsAsync(long roomId, string? accountId, Func<RecordedEventEnvelope, CancellationToken, Task> onEvent, CancellationToken cancellationToken = default)
    {
        var room = Rooms[roomId];
        room.OpenSubscription();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var sequence = room.NextEvent();
                await onEvent(new() { EventId = $"fixture:{roomId}:{sequence}", Kind = "Danmu", Command = "DANMU_MSG", ReceivedAtUtc = DateTime.UtcNow, SourceTimeUtc = DateTime.UtcNow, UserId = "42", UserName = "测试观众", Text = $"弹幕 {sequence}", RawJson = "{\"cmd\":\"DANMU_MSG\"}" }, cancellationToken);
                await Task.Delay(200, cancellationToken);
            }
        }
        finally
        {
            room.CloseSubscription();
        }
    }

    public Task<IReadOnlyList<BilibiliAccountSummary>> ListAccountsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<BilibiliLoginSession> BeginQrAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<BilibiliLoginSession> PollQrAsync(string sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<BilibiliLoginSession> BeginSmsAsync(BilibiliSmsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<BilibiliLoginSession> SendSmsAsync(string sessionId, BilibiliChallengeResponse response, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<BilibiliLoginSession> CompleteSmsAsync(string sessionId, string code, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DeleteAccountAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
