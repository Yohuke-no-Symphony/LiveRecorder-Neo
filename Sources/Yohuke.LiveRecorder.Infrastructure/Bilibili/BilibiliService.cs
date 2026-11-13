using Yohuke.LiveRecorder.Application.Localization;
using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Yohuke.DanmuCore.Apis;
using Yohuke.DanmuCore.Models.Accounts;
using Yohuke.DanmuCore.Services;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Bilibili;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Persistence;

namespace Yohuke.LiveRecorder.Infrastructure.Bilibili;

/// <summary>隔离后台采集身份，并将 Core 接入录制和文件事件流。</summary>
public sealed class BilibiliService(IMongoContext context, ISecretProtector protector, IHttpClientFactory httpClientFactory, ILogger<BilibiliService> logger) : IBilibiliService, IDisposable
{
    private readonly ConcurrentDictionary<string, BilibiliBindingSession> sessions = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> credentialLifetimes = new();
    private readonly ConcurrentBag<CancellationTokenSource> retiredLifetimes = [];
    private readonly SemaphoreSlim accountGate = new(1, 1);
    private IMongoCollection<BilibiliAccount> Accounts => context.Collection<BilibiliAccount>("bilibili_accounts");

    /// <inheritdoc />
    public async Task<LiveRoomSnapshot> GetRoomAsync(long roomId, string? accountId = null, CancellationToken cancellationToken = default)
    {
        if (roomId <= 0)
        {
            throw new InfrastructureException(MessageCode.BILIBILI_ROOM_ID_INVALID);
        }
        var (_, credentials) = await GetCredentialsAsync(accountId, cancellationToken);
        using var client = httpClientFactory.CreateClient("bilibili");
        using var api = new BilibiliApi(credentials.Cookie, httpClient: client);
        var response = await api.GetRoomInfoAsync(roomId, cancellationToken: cancellationToken);
        if (response.Code != 0 || response.Data?.RoomInfo is not { } room)
        {
            throw new InfrastructureException(MessageCode.BILIBILI_ROOM_UNAVAILABLE, 502, response.Code);
        }
        var anchor = response.Data.AnchorInfo?.BaseInfo;
        var started = room.LiveStartTime;
        if (room.LiveStatus == 1 && started <= 0)
        {
            var play = await api.GetRoomPlayInfoAsync(room.RoomId, cookie: credentials.Cookie, cancellationToken: cancellationToken);
            started = play.Data?.LiveTime ?? 0;
        }
        return new(room.RoomId, room.Uid, anchor?.Name ?? room.Uid.ToString(), NormalizeImage(anchor?.Face), room.Title ?? "直播录像", NormalizeImage(room.CoverUrl ?? room.UserCoverUrl), room.LiveStatus == 1, started is > 0 and <= 253402300799 ? DateTimeOffset.FromUnixTimeSeconds(started).UtcDateTime : null);
    }

    /// <inheritdoc />
    public async Task<LiveStreamDescriptor> ResolveStreamAsync(long roomId, int quality, string? accountId = null, CancellationToken cancellationToken = default)
    {
        var (_, credentials) = await GetCredentialsAsync(accountId, cancellationToken);
        using var client = httpClientFactory.CreateClient("bilibili");
        using var api = new BilibiliApi(credentials.Cookie, httpClient: client);
        var response = await api.GetRoomPlayInfoAsync(roomId, quality, credentials.Cookie, cancellationToken: cancellationToken);
        if (response.Code != 0 || response.Data?.LiveStatus != 1)
        {
            throw new InfrastructureException(MessageCode.BILIBILI_STREAM_UNAVAILABLE, 503, response.Code);
        }
        var streams = response.Data.PlayUrlInfo?.PlayUrl?.Streams ?? [];
        foreach (var stream in streams.Where(x => x.ProtocolName == "http_stream"))
        {
            foreach (var format in stream.Formats.Where(x => x.FormatName == "flv"))
            {
                foreach (var codec in format.Codecs.OrderByDescending(x => x.CodecName == "avc"))
                {
                    foreach (var host in codec.UrlInfos)
                    {
                        var value = host.Host + codec.BaseUrl + host.Extra;
                        if (Uri.TryCreate(value, UriKind.Absolute, out var url) && url.Scheme is "http" or "https" && string.IsNullOrEmpty(url.UserInfo))
                        {
                            return new(value, new Dictionary<string, string> { ["User-Agent"] = BaseApi.DEFAULT_USER_AGENT, ["Referer"] = $"https://live.bilibili.com/{roomId}" }, codec.CurrentQuality, codec.CodecName, format.FormatName);
                        }
                    }
                }
            }
        }
        throw new InfrastructureException(MessageCode.BILIBILI_FLV_STREAM_MISSING, 503);
    }

    /// <inheritdoc />
    public async Task SubscribeEventsAsync(long roomId, string? accountId, Func<RecordedEventEnvelope, CancellationToken, Task> onEvent, CancellationToken cancellationToken = default)
    {
        var reconciliation = new BilibiliDanmuReconciliation();
        while (!cancellationToken.IsCancellationRequested)
        {
            var (account, credentials) = await GetCredentialsAsync(accountId, cancellationToken);
            var changed = account is null ? CancellationToken.None : credentialLifetimes.GetOrAdd(account.Id, _ => new()).Token;
            if (account is not null)
            {
                var current = await Accounts.Find(x => x.Id == account.Id).FirstOrDefaultAsync(cancellationToken);
                if (current is null || current.ProtectedCredentials != account.ProtectedCredentials || current.Status == "Expired")
                {
                    continue;
                }
            }
            using var socket = new BilibiliLiveWebSocket(roomId, credentials.Cookie);
            using var recoveryHttp = httpClientFactory.CreateClient("bilibili");
            using var subscription = new BilibiliEventSubscription(socket, socket, roomId, credentials.Cookie, new BilibiliDanmuHistoryService(recoveryHttp), new BilibiliRedPocketService(recoveryHttp), logger, cancellationToken, changed, reconciliation: reconciliation);
            await subscription.RunAsync(onEvent, cancellationToken);
            if (!changed.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BilibiliAccountSummary>> ListAccountsAsync(CancellationToken cancellationToken = default) =>
        (await Accounts.Find(FilterDefinition<BilibiliAccount>.Empty).SortBy(x => x.CreatedAtUtc).ToListAsync(cancellationToken)).Select(Profile).ToArray();

    /// <inheritdoc />
    public async Task<BilibiliLoginSession> BeginQrAsync(CancellationToken cancellationToken = default)
    {
        var session = CreateSession();
        try
        {
            var qr = await session.Client.BeginQrAsync(cancellationToken);
            session.QrKey = qr.Key;
            session.QrUrl = qr.Url;
            return new(session.Id, "Waiting", session.ExpiresAtUtc, QrUrl: qr.Url);
        }
        catch
        {
            RemoveSession(session.Id);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<BilibiliLoginSession> PollQrAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var session = RequireSession(sessionId);
        await session.Gate.WaitAsync(cancellationToken);
        try
        {
            if (session.Completed is not null)
            {
                return session.Completed;
            }
            if (session.QrKey is null)
            {
                throw new InfrastructureException(MessageCode.BILIBILI_QR_SESSION_REQUIRED);
            }
            return await ApplyLoginResultAsync(session, await session.Client.PollQrAsync(session.QrKey, cancellationToken), cancellationToken);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<BilibiliLoginSession> BeginSmsAsync(BilibiliSmsRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Cid) || request.Cid.Length > 5 || !request.Cid.All(char.IsAsciiDigit) || string.IsNullOrWhiteSpace(request.Tel) || request.Tel.Length is < 5 or > 20 || !request.Tel.All(char.IsAsciiDigit))
        {
            throw new InfrastructureException(MessageCode.BILIBILI_PHONE_INVALID);
        }
        var session = CreateSession();
        try
        {
            session.Cid = request.Cid;
            session.Tel = request.Tel;
            session.Challenge = await session.Client.GetChallengeAsync(cancellationToken);
            return new(session.Id, "Challenge", session.ExpiresAtUtc, Challenge: Challenge(session.Challenge));
        }
        catch
        {
            RemoveSession(session.Id);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<BilibiliLoginSession> SendSmsAsync(string sessionId, BilibiliChallengeResponse response, CancellationToken cancellationToken = default)
    {
        var session = RequireSession(sessionId);
        await session.Gate.WaitAsync(cancellationToken);
        try
        {
            if (session.Completed is not null || session.Challenge is null)
            {
                throw new InfrastructureException(MessageCode.BILIBILI_SMS_SESSION_UNAVAILABLE);
            }
            if (session.LastSentAtUtc > DateTime.UtcNow.AddSeconds(-60))
            {
                throw new InfrastructureException(MessageCode.BILIBILI_SMS_RATE_LIMIT, 429);
            }
            var proof = new BilibiliChallengeProof { Token = response.Token, Challenge = response.Challenge, Validate = response.Validate, Seccode = response.Seccode, LotNumber = response.LotNumber, CaptchaOutput = response.CaptchaOutput, PassToken = response.PassToken, GenTime = response.GenTime, Captcha = response.Captcha };
            session.CaptchaKey = await session.Client.SendSmsAsync(session.Cid, session.Tel, session.Challenge, proof, cancellationToken);
            session.LastSentAtUtc = DateTime.UtcNow;
            return new(session.Id, "SmsSent", session.ExpiresAtUtc);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<BilibiliLoginSession> CompleteSmsAsync(string sessionId, string code, CancellationToken cancellationToken = default)
    {
        var session = RequireSession(sessionId);
        await session.Gate.WaitAsync(cancellationToken);
        try
        {
            if (session.Completed is not null)
            {
                return session.Completed;
            }
            if (string.IsNullOrWhiteSpace(session.CaptchaKey) || string.IsNullOrWhiteSpace(code))
            {
                throw new InfrastructureException(MessageCode.BILIBILI_SMS_CODE_REQUIRED);
            }
            return await ApplyLoginResultAsync(session, await session.Client.CompleteSmsAsync(session.Cid, session.Tel, session.CaptchaKey, code, cancellationToken), cancellationToken);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task DeleteAccountAsync(string id, CancellationToken cancellationToken = default)
    {
        if (await context.Collection<Streamer>("streamers").Find(x => x.AccountId == id && x.Enabled).AnyAsync(cancellationToken))
        {
            throw new InfrastructureException(MessageCode.BILIBILI_ACCOUNT_IN_USE, 409);
        }
        await Accounts.DeleteOneAsync(x => x.Id == id, cancellationToken);
        RotateLifetime(id);
    }

    internal async Task MaintainAccountsAsync(CancellationToken cancellationToken)
    {
        PruneSessions();
        var accounts = await Accounts.Find(FilterDefinition<BilibiliAccount>.Empty).ToListAsync(cancellationToken);
        foreach (var account in accounts)
        {
            try
            {
                var credentials = DeserializeCredentials(account);
                using var http = httpClientFactory.CreateClient("bilibili");
                var validation = await new BilibiliAccountValidationService(http).ValidateAsync(credentials.Cookie, cancellationToken: cancellationToken);
                var status = validation.Status switch
                {
                    AccountValidationStatus.Valid => "Valid",
                    AccountValidationStatus.Expired or AccountValidationStatus.Incomplete => "Expired",
                    _ => "Unavailable"
                };
                await Accounts.UpdateOneAsync(x => x.Id == account.Id && x.ProtectedCredentials == account.ProtectedCredentials, Builders<BilibiliAccount>.Update.Set(x => x.Status, status).Set(x => x.LastValidatedAtUtc, DateTime.UtcNow), cancellationToken: cancellationToken);
                if (status != "Valid")
                {
                    if (status == "Expired")
                    {
                        RotateLifetime(account.Id);
                    }
                    continue;
                }
                await UpdateMissingAvatarAsync(account, cancellationToken);
                using var client = new BilibiliWebLoginClient(httpClientFactory.CreateClient("bilibili-login"), true);
                var refreshed = await client.RefreshAsync(credentials, cancellationToken);
                if (refreshed != credentials)
                {
                    var saved = await Accounts.UpdateOneAsync(x => x.Id == account.Id && x.ProtectedCredentials == account.ProtectedCredentials, Builders<BilibiliAccount>.Update.Set(x => x.ProtectedCredentials, protector.Protect(JsonSerializer.Serialize(refreshed))), cancellationToken: CancellationToken.None);
                    if (saved.ModifiedCount > 0)
                    {
                        RotateLifetime(account.Id);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning("B站账号 {AccountId} 维护失败：{ErrorType}", account.Id, exception.GetType().Name);
            }
        }
    }

    /// <summary>停止登录会话并释放凭据变更边界。</summary>
    public void Dispose()
    {
        foreach (var session in sessions.Values)
        {
            session.Dispose();
        }
        foreach (var lifetime in credentialLifetimes.Values.Concat(retiredLifetimes))
        {
            lifetime.Cancel();
            lifetime.Dispose();
        }
    }

    private async Task<BilibiliLoginSession> ApplyLoginResultAsync(BilibiliBindingSession session, BilibiliWebLoginResult result, CancellationToken cancellationToken)
    {
        if (result.Credentials is { } credentials)
        {
            var account = await SaveAccountAsync(credentials, cancellationToken);
            session.Completed = new(session.Id, "Complete", session.ExpiresAtUtc, Account: account);
            session.Dispose();
            return session.Completed;
        }
        return new(session.Id, result.Status, session.ExpiresAtUtc, session.QrUrl, Message: result.Message, VerificationUrl: result.VerificationUrl);
    }

    private async Task<BilibiliAccountSummary> SaveAccountAsync(BilibiliLoginCredentials credentials, CancellationToken cancellationToken)
    {
        using var client = httpClientFactory.CreateClient("bilibili");
        using var api = new BilibiliApi(credentials.Cookie, httpClient: client);
        var navigation = await api.GetNavigationAsync(cancellationToken: cancellationToken);
        if (navigation.Code != 0 || navigation.Data is not { IsLogin: true, Uid: > 0 } user)
        {
            throw new InfrastructureException(MessageCode.BILIBILI_IDENTITY_NOT_CONFIRMED);
        }
        var avatar = await GetAvatarUrlAsync(user.Uid, cancellationToken);
        await accountGate.WaitAsync(cancellationToken);
        try
        {
            await Accounts.Indexes.CreateOneAsync(new CreateIndexModel<BilibiliAccount>(Builders<BilibiliAccount>.IndexKeys.Ascending(x => x.Uid), new CreateIndexOptions { Unique = true }), cancellationToken: cancellationToken);
            var account = await Accounts.Find(x => x.Uid == user.Uid).FirstOrDefaultAsync(cancellationToken) ?? new BilibiliAccount { Uid = user.Uid };
            account.UserName = user.UserName;
            if (!string.IsNullOrWhiteSpace(avatar))
            {
                account.AvatarUrl = avatar;
            }
            account.ProtectedCredentials = protector.Protect(JsonSerializer.Serialize(credentials));
            account.Status = "Valid";
            account.LastValidatedAtUtc = DateTime.UtcNow;
            await Accounts.ReplaceOneAsync(x => x.Id == account.Id, account, new ReplaceOptions { IsUpsert = true }, cancellationToken);
            RotateLifetime(account.Id);
            return Profile(account);
        }
        finally
        {
            accountGate.Release();
        }
    }

    private async Task UpdateMissingAvatarAsync(BilibiliAccount account, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(account.AvatarUrl))
        {
            return;
        }
        var avatar = await GetAvatarUrlAsync(account.Uid, cancellationToken);
        if (!string.IsNullOrWhiteSpace(avatar))
        {
            await Accounts.UpdateOneAsync(x => x.Id == account.Id && x.AvatarUrl == account.AvatarUrl,
                Builders<BilibiliAccount>.Update.Set(x => x.AvatarUrl, avatar), cancellationToken: cancellationToken);
        }
    }

    private async Task<string> GetAvatarUrlAsync(long uid, CancellationToken cancellationToken)
    {
        using var client = httpClientFactory.CreateClient("bilibili");
        using var source = new BilibiliUserAvatarSource(client);
        return await source.GetAvatarUrlAsync(uid, cancellationToken);
    }

    private async Task<(BilibiliAccount? Account, BilibiliLoginCredentials Credentials)> GetCredentialsAsync(string? accountId, CancellationToken cancellationToken)
    {
        var account = accountId is null
            ? await Accounts.Find(x => x.Status == "Valid").SortBy(x => x.CreatedAtUtc).FirstOrDefaultAsync(cancellationToken)
              ?? await Accounts.Find(FilterDefinition<BilibiliAccount>.Empty).SortBy(x => x.CreatedAtUtc).FirstOrDefaultAsync(cancellationToken)
            : await Accounts.Find(x => x.Id == accountId).FirstOrDefaultAsync(cancellationToken) ?? throw new InfrastructureException(MessageCode.BILIBILI_ACCOUNT_NOT_FOUND, 409);
        if (account is not null && account.Status == "Expired")
        {
            throw new InfrastructureException(MessageCode.BILIBILI_ACCOUNT_EXPIRED, 409);
        }
        return (account, account is null ? new() : DeserializeCredentials(account));
    }

    private BilibiliLoginCredentials DeserializeCredentials(BilibiliAccount account) => JsonSerializer.Deserialize<BilibiliLoginCredentials>(protector.Unprotect(account.ProtectedCredentials)) ?? throw new InfrastructureException(MessageCode.BILIBILI_CREDENTIALS_CORRUPT, 503);

    private BilibiliBindingSession CreateSession()
    {
        PruneSessions();
        if (sessions.Count >= 50)
        {
            throw new InfrastructureException(MessageCode.BILIBILI_TOO_MANY_LOGIN_FLOWS, 429);
        }
        var session = new BilibiliBindingSession(new(httpClientFactory.CreateClient("bilibili-login"), true));
        sessions[session.Id] = session;
        return session;
    }

    private BilibiliBindingSession RequireSession(string id)
    {
        if (!sessions.TryGetValue(id, out var session) || session.ExpiresAtUtc <= DateTime.UtcNow)
        {
            throw new InfrastructureException(MessageCode.BILIBILI_LOGIN_EXPIRED, 410);
        }
        return session;
    }

    private void PruneSessions()
    {
        foreach (var session in sessions.Values.Where(x => x.ExpiresAtUtc <= DateTime.UtcNow))
        {
            RemoveSession(session.Id);
        }
    }

    private void RemoveSession(string id)
    {
        if (sessions.TryRemove(id, out var session))
        {
            session.Dispose();
        }
    }

    private void RotateLifetime(string id)
    {
        credentialLifetimes.AddOrUpdate(id, _ => new(), (_, previous) =>
        {
            previous.Cancel();
            retiredLifetimes.Add(previous);
            return new();
        });
    }

    private static BilibiliAccountSummary Profile(BilibiliAccount account) => new(account.Id, account.Uid, account.UserName, account.AvatarUrl, account.Status, account.LastValidatedAtUtc);
    private static BilibiliChallenge Challenge(BilibiliLoginChallenge challenge) => new(challenge.Type, challenge.Token, challenge.Gt, challenge.Challenge, challenge.ImageUrl);
    private static string NormalizeImage(string? value) => string.IsNullOrWhiteSpace(value) ? "" : value.StartsWith("//", StringComparison.Ordinal) ? "https:" + value : value.Replace("http://", "https://", StringComparison.OrdinalIgnoreCase);
}
