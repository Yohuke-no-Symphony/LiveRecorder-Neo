using Microsoft.Extensions.Logging;
using Yohuke.DanmuCore.Models.Accounts;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Infrastructure.Bilibili;

internal sealed class BilibiliEventAccountRunner(
    IBilibiliEventConnection connection,
    Func<CancellationToken, Task<BilibiliAccountSelection>> selectAccounts,
    Func<BilibiliAccount, BilibiliLoginCredentials> readCredentials,
    Func<BilibiliAccount, CancellationToken, Task<bool>> expireCredentials,
    Func<Task> accountChanges,
    ILogger logger,
    BilibiliDanmuReconciliation? reconciliation = null)
{
    private const int INITIAL_RETRY_SECONDS = 2;
    private const int MAX_RETRY_SECONDS = 30;
    private readonly BilibiliDanmuReconciliation reconciliation = reconciliation ?? new();
    private readonly Dictionary<string, BilibiliAccountFailure> failures = new(StringComparer.Ordinal);
    private bool anonymousFailed;
    private int gapReported;

    internal async Task RunAsync(long roomId, Func<RecordedEventEnvelope, CancellationToken, Task> onEvent, CancellationToken cancellationToken)
    {
        var retrySeconds = INITIAL_RETRY_SECONDS;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var changed = accountChanges();
                var selection = await selectAccounts(cancellationToken);
                var account = SelectCandidate(selection);
                if (account is null && (!selection.AllowsAnonymous || anonymousFailed))
                {
                    await ReportGapAsync(onEvent, cancellationToken);
                    if (await WaitForRetryAsync(changed, retrySeconds, cancellationToken))
                    {
                        failures.Clear();
                        anonymousFailed = false;
                        retrySeconds = Math.Min(MAX_RETRY_SECONDS, retrySeconds * 2);
                    }
                    continue;
                }
                BilibiliLoginCredentials credentials;
                try
                {
                    credentials = account is null ? new() : readCredentials(account);
                }
                catch (InfrastructureException error) when (error.Code == nameof(MessageCode.BILIBILI_CREDENTIALS_CORRUPT))
                {
                    Reject(account);
                    logger.LogWarning("直播间 {RoomId} 账号 {AccountId} 凭据不可读，尝试下一有效账号", roomId, account?.Id);
                    await ReportGapAsync(onEvent, cancellationToken);
                    continue;
                }
                try
                {
                    var reselected = await RunConnectionAsync(roomId, account, credentials.Cookie, changed, onEvent, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!reselected)
                    {
                        throw new BilibiliEventSourceException(new EndOfStreamException("The event source ended before cancellation."));
                    }
                    retrySeconds = INITIAL_RETRY_SECONDS;
                }
                catch (BilibiliEventSourceException error)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Reject(account);
                    if (account is not null && error.IsAuthenticationFailure)
                    {
                        await expireCredentials(account, cancellationToken);
                    }
                    logger.LogWarning("直播间 {RoomId} 账号 {AccountId} 弹幕来源失败，尝试下一有效账号：{ErrorType}", roomId, account?.Id, error.InnerException?.GetType().Name);
                    await ReportGapAsync(onEvent, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task<bool> RunConnectionAsync(long roomId, BilibiliAccount? account, string cookie, Task changed,
        Func<RecordedEventEnvelope, CancellationToken, Task> onEvent, CancellationToken cancellationToken)
    {
        if (changed.IsCompleted)
        {
            return true;
        }
        using var credentialsChanged = new CancellationTokenSource();
        var capture = connection.RunAsync(roomId, cookie, async (item, token) =>
        {
            if (item.Kind == "Gap")
            {
                Interlocked.Exchange(ref gapReported, 1);
            }
            else if (item.Kind is "Danmu" or "Gift" or "SuperChat" or "Guard" or "RedPocket")
            {
                Interlocked.Exchange(ref gapReported, 0);
            }
            await onEvent(item, token);
        }, reconciliation, cancellationToken, credentialsChanged.Token);
        try
        {
            while (true)
            {
                await Task.WhenAny(capture, changed).WaitAsync(cancellationToken);
                if (capture.IsCompleted)
                {
                    return false;
                }
                changed = accountChanges();
                var selection = await selectAccounts(cancellationToken);
                var next = SelectCandidate(selection);
                if (next is null && !selection.AllowsAnonymous
                    || next?.Id != account?.Id
                    || next?.ProtectedCredentials != account?.ProtectedCredentials)
                {
                    return true;
                }
            }
        }
        finally
        {
            await credentialsChanged.CancelAsync();
            await capture;
        }
    }

    private BilibiliAccount? SelectCandidate(BilibiliAccountSelection selection)
    {
        foreach (var account in selection.Candidates)
        {
            if (failures.TryGetValue(account.Id, out var failure))
            {
                if (account.ProtectedCredentials == failure.ProtectedCredentials
                    && !(account.LastValidatedAtUtc > failure.FailedAtUtc))
                {
                    continue;
                }
                failures.Remove(account.Id);
            }
            return account;
        }
        return null;
    }

    private void Reject(BilibiliAccount? account)
    {
        if (account is null)
        {
            anonymousFailed = true;
        }
        else
        {
            failures[account.Id] = new(account.ProtectedCredentials, DateTime.UtcNow);
        }
    }

    private async Task ReportGapAsync(Func<RecordedEventEnvelope, CancellationToken, Task> onEvent, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref gapReported, 1) != 0)
        {
            return;
        }
        await onEvent(new RecordedEventEnvelope
        {
            Kind = "Gap",
            ReceivedAtUtc = DateTime.UtcNow,
            TextCode = nameof(MessageCode.RECORDING_EVENT_CONNECTION_LOST),
            Text = LocalizedText.Format(nameof(MessageCode.RECORDING_EVENT_CONNECTION_LOST), "")
        }, cancellationToken);
    }

    private static async Task<bool> WaitForRetryAsync(Task changed, int retrySeconds, CancellationToken cancellationToken)
    {
        using var timerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delay = Task.Delay(TimeSpan.FromSeconds(retrySeconds), timerCancellation.Token);
        try
        {
            return await Task.WhenAny(changed, delay).WaitAsync(cancellationToken) == delay;
        }
        finally
        {
            await timerCancellation.CancelAsync();
        }
    }
}
