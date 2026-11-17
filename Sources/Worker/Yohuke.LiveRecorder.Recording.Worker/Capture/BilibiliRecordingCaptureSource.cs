using Microsoft.Extensions.Logging.Abstractions;
using Yohuke.DanmuCore.Apis;
using Yohuke.DanmuCore.Models.Accounts;
using Yohuke.LiveRecorder.Application.Bilibili;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Bilibili;
using Yohuke.LiveRecorder.Recording.Contracts;

namespace Yohuke.LiveRecorder.Recording.Worker.Capture;

internal sealed class BilibiliRecordingCaptureSource : IRecordingCaptureSource
{
    private readonly RecordingHttpClientFactory clients = new();
    private readonly BilibiliDanmuReconciliation reconciliation = new();
    private readonly IBilibiliEventConnection eventConnection;

    internal BilibiliRecordingCaptureSource(IBilibiliEventConnection? eventConnection = null)
    {
        this.eventConnection = eventConnection ?? new BilibiliEventConnection(clients, NullLogger<BilibiliEventConnection>.Instance);
    }

    public Task<LiveRoomSnapshot> GetRoomAsync(RecordingAssignment assignment, CancellationToken cancellationToken) =>
        ExecuteAsync(assignment, (api, credentials) => BilibiliRecordingSource.GetRoomAsync(api, credentials, assignment.RoomId,
            cancellationToken), cancellationToken);

    public Task<LiveStreamDescriptor> ResolveStreamAsync(RecordingAssignment assignment, CancellationToken cancellationToken) =>
        ExecuteAsync(assignment, (api, credentials) => BilibiliRecordingSource.ResolveStreamAsync(api, credentials,
            assignment.RoomId, assignment.Quality, cancellationToken), cancellationToken);

    public Task SubscribeEventsAsync(RecordingAssignment assignment,
        Func<RecordedEventEnvelope, CancellationToken, Task> onEvent, CancellationToken cancellationToken)
    {
        var accounts = assignment.Accounts.Select((account, index) => new BilibiliAccount
        {
            Id = account.AccountId,
            Uid = account.Uid,
            Status = "Valid",
            ProtectedCredentials = account.Cookie,
            CreatedAtUtc = DateTime.UnixEpoch.AddTicks(index)
        }).ToArray();
        var neverChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return new BilibiliEventAccountRunner(eventConnection,
            _ => Task.FromResult(new BilibiliAccountSelection(accounts, null, null)),
            account => new BilibiliLoginCredentials { Cookie = account.ProtectedCredentials },
            (account, _) =>
            {
                account.Status = "Expired";
                return Task.FromResult(true);
            }, () => neverChanged.Task, NullLogger.Instance, reconciliation).RunAsync(assignment.RoomId, onEvent, cancellationToken);
    }

    private async Task<T> ExecuteAsync<T>(RecordingAssignment assignment,
        Func<BilibiliApi, BilibiliLoginCredentials, Task<T>> request, CancellationToken cancellationToken)
    {
        Exception? last = null;
        var cookies = assignment.Accounts.Count == 0 ? [""] : assignment.Accounts.Select(account => account.Cookie).ToArray();
        foreach (var cookie in cookies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var client = clients.CreateClient("bilibili");
                using var api = new BilibiliApi(cookie, httpClient: client);
                return await request(api, new() { Cookie = cookie });
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                last = error;
            }
        }
        throw last ?? new InvalidOperationException("No recording capture identity is available.");
    }
}
