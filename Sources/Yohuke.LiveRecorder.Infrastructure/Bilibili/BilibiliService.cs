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
public sealed class BilibiliService(IMongoContext context, ISecretProtector protector, IHttpClientFactory httpClientFactory, ILogger<BilibiliService> logger, ISettingsStore settingsStore, IBilibiliEventConnection eventConnection) : IBilibiliService, IDisposable
{
    private readonly ConcurrentDictionary<string, BilibiliBindingSession> sessions = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> credentialLifetimes = new();
    private readonly ConcurrentBag<CancellationTokenSource> retiredLifetimes = [];
    private readonly SemaphoreSlim accountGate = new(1, 1);
    private readonly object lifetimeGate = new();
    private CancellationTokenSource defaultLifetime = new();
    private int disposed;
    private IMongoCollection<BilibiliAccount> Accounts => context.Collection<BilibiliAccount>("bilibili_accounts");

    /// <inheritdoc />
    public async Task<BilibiliRoomSearchPage> SearchRoomsAsync(string keyword, int page = 1, string? accountId = null, CancellationToken cancellationToken = default)
    {
        var (_, credentials) = await GetCredentialsAsync(accountId, cancellationToken);
        using var client = httpClientFactory.CreateClient("bilibili");
        return await new BilibiliRoomSearchService(client).SearchAsync(keyword, page, credentials.Cookie, cancellationToken);
    }

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
            var defaultChanged = accountId is null ? GetLifetimeToken(null) : CancellationToken.None;
            var (account, credentials) = await GetCredentialsAsync(accountId, cancellationToken);
            var accountChanged = account is null ? CancellationToken.None : GetLifetimeToken(account.Id);
            using var changes = CancellationTokenSource.CreateLinkedTokenSource(defaultChanged, accountChanged);
            var changed = changes.Token;
            if (changed.IsCancellationRequested)
            {
                continue;
            }
            if (account is not null)
            {
                var current = await Accounts.Find(x => x.Id == account.Id).FirstOrDefaultAsync(cancellationToken);
                if (current is null || current.ProtectedCredentials != account.ProtectedCredentials || current.Status == "Expired")
                {
                    continue;
                }
            }
            await eventConnection.RunAsync(roomId, credentials.Cookie, onEvent, reconciliation, cancellationToken, changed);
            if (!changed.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BilibiliAccountSummary>> ListAccountsAsync(CancellationToken cancellationToken = default)
    {
        await accountGate.WaitAsync(cancellationToken);
        try
        {
            return await ListAccountsCoreAsync(cancellationToken);
        }
        finally
        {
            accountGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BilibiliAccountSummary>> SetDefaultAccountAsync(string? accountId, CancellationToken cancellationToken = default)
    {
        await accountGate.WaitAsync(cancellationToken);
        try
        {
            if (accountId is not null)
            {
                RequireValidAccount(await RequireAccountAsync(accountId, cancellationToken));
            }
            var previous = await EffectiveAccountIdAsync(cancellationToken);
            await settingsStore.UpdateAsync(settings =>
            {
                settings.DefaultBilibiliAccountId = accountId;
                return settings;
            }, cancellationToken);
            await NotifyDefaultChangeAsync(previous, CancellationToken.None);
            return await ListAccountsCoreAsync(cancellationToken);
        }
        finally
        {
            accountGate.Release();
        }
    }

    /// <inheritdoc />
    public Task<BilibiliLoginSession> BeginQrAsync(CancellationToken cancellationToken = default) => BeginQrCoreAsync(null, cancellationToken);

    /// <inheritdoc />
    public Task<BilibiliLoginSession> BeginAccountQrAsync(string accountId, CancellationToken cancellationToken = default) => BeginQrCoreAsync(accountId, cancellationToken);

    private async Task<BilibiliLoginSession> BeginQrCoreAsync(string? accountId, CancellationToken cancellationToken)
    {
        var session = await CreateSessionAsync(accountId, cancellationToken);
        await session.Gate.WaitAsync(cancellationToken);
        try
        {
            var qr = await session.Client.BeginQrAsync(cancellationToken);
            RequireAvailableSession(session);
            session.QrKey = qr.Key;
            session.QrUrl = qr.Url;
            return new(session.Id, "Waiting", session.ExpiresAtUtc, QrUrl: qr.Url);
        }
        catch
        {
            RemoveSession(session.Id);
            throw;
        }
        finally
        {
            session.Gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<BilibiliLoginSession> PollQrAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var session = RequireSession(sessionId);
        await session.Gate.WaitAsync(cancellationToken);
        try
        {
            RequireAvailableSession(session);
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
    public Task<BilibiliLoginSession> BeginSmsAsync(BilibiliSmsRequest request, CancellationToken cancellationToken = default) => BeginSmsCoreAsync(null, request, cancellationToken);

    /// <inheritdoc />
    public Task<BilibiliLoginSession> BeginAccountSmsAsync(string accountId, BilibiliSmsRequest request, CancellationToken cancellationToken = default) => BeginSmsCoreAsync(accountId, request, cancellationToken);

    private async Task<BilibiliLoginSession> BeginSmsCoreAsync(string? accountId, BilibiliSmsRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Cid) || request.Cid.Length > 5 || !request.Cid.All(char.IsAsciiDigit) || string.IsNullOrWhiteSpace(request.Tel) || request.Tel.Length is < 5 or > 20 || !request.Tel.All(char.IsAsciiDigit))
        {
            throw new InfrastructureException(MessageCode.BILIBILI_PHONE_INVALID);
        }
        var session = await CreateSessionAsync(accountId, cancellationToken);
        await session.Gate.WaitAsync(cancellationToken);
        try
        {
            session.Cid = request.Cid;
            session.Tel = request.Tel;
            session.Challenge = await session.Client.GetChallengeAsync(cancellationToken);
            RequireAvailableSession(session);
            return new(session.Id, "Challenge", session.ExpiresAtUtc, Challenge: Challenge(session.Challenge));
        }
        catch
        {
            RemoveSession(session.Id);
            throw;
        }
        finally
        {
            session.Gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<BilibiliLoginSession> SendSmsAsync(string sessionId, BilibiliChallengeResponse response, CancellationToken cancellationToken = default)
    {
        var session = RequireSession(sessionId);
        await session.Gate.WaitAsync(cancellationToken);
        try
        {
            RequireAvailableSession(session);
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
            RequireAvailableSession(session);
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
            RequireAvailableSession(session);
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
        await accountGate.WaitAsync(cancellationToken);
        try
        {
            if (await context.Collection<Streamer>("streamers").Find(x => x.AccountId == id && x.Enabled).AnyAsync(cancellationToken))
            {
                throw new InfrastructureException(MessageCode.BILIBILI_ACCOUNT_IN_USE, 409);
            }
            var previous = await EffectiveAccountIdAsync(cancellationToken);
            await settingsStore.UpdateAsync(settings =>
            {
                if (settings.DefaultBilibiliAccountId == id)
                {
                    settings.DefaultBilibiliAccountId = null;
                }
                return settings;
            }, cancellationToken);
            await Accounts.DeleteOneAsync(x => x.Id == id, CancellationToken.None);
            foreach (var session in sessions.Values.Where(x => x.AccountId == id))
            {
                session.Invalidate();
            }
            RotateLifetime(id);
            await NotifyDefaultChangeAsync(previous, CancellationToken.None);
        }
        finally
        {
            accountGate.Release();
        }
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
                if (!await SaveValidationAsync(account, status, cancellationToken) || status != "Valid")
                {
                    continue;
                }
                await UpdateMissingAvatarAsync(account, cancellationToken);
                using var client = new BilibiliWebLoginClient(httpClientFactory.CreateClient("bilibili-login"), true);
                var refreshed = await client.RefreshAsync(credentials, cancellationToken);
                if (refreshed != credentials)
                {
                    await accountGate.WaitAsync(cancellationToken);
                    try
                    {
                        var saved = await Accounts.UpdateOneAsync(x => x.Id == account.Id && x.ProtectedCredentials == account.ProtectedCredentials,
                            Builders<BilibiliAccount>.Update.Set(x => x.ProtectedCredentials, protector.Protect(JsonSerializer.Serialize(refreshed))), cancellationToken: CancellationToken.None);
                        if (saved.ModifiedCount > 0)
                        {
                            RotateLifetime(account.Id);
                        }
                    }
                    finally
                    {
                        accountGate.Release();
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
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        foreach (var session in sessions.Values)
        {
            session.Dispose();
        }
        lock (lifetimeGate)
        {
            foreach (var lifetime in credentialLifetimes.Values.Concat(retiredLifetimes).Append(defaultLifetime))
            {
                lifetime.Cancel();
                lifetime.Dispose();
            }
        }
    }

    private async Task<BilibiliLoginSession> ApplyLoginResultAsync(BilibiliBindingSession session, BilibiliWebLoginResult result, CancellationToken cancellationToken)
    {
        RequireAvailableSession(session);
        if (result.Credentials is { } credentials)
        {
            var account = await SaveAccountAsync(session, credentials, cancellationToken);
            session.Completed = new(session.Id, "Complete", session.ExpiresAtUtc, Account: account);
            session.Dispose();
            return session.Completed;
        }
        return new(session.Id, result.Status, session.ExpiresAtUtc, session.QrUrl, Message: result.Message, VerificationUrl: result.VerificationUrl);
    }

    private async Task<BilibiliAccountSummary> SaveAccountAsync(BilibiliBindingSession session, BilibiliLoginCredentials credentials, CancellationToken cancellationToken)
    {
        using var client = httpClientFactory.CreateClient("bilibili");
        using var api = new BilibiliApi(credentials.Cookie, httpClient: client);
        var navigation = await api.GetNavigationAsync(cancellationToken: cancellationToken);
        if (navigation.Code != 0 || navigation.Data is not { IsLogin: true, Uid: > 0 } user)
        {
            throw new InfrastructureException(MessageCode.BILIBILI_IDENTITY_NOT_CONFIRMED);
        }
        if (session.Uid is { } expectedUid && expectedUid != user.Uid)
        {
            throw new InfrastructureException(MessageCode.BILIBILI_RELOGIN_IDENTITY_MISMATCH, 409);
        }
        var avatar = await GetAvatarUrlAsync(user.Uid, cancellationToken);
        await accountGate.WaitAsync(cancellationToken);
        try
        {
            RequireAvailableSession(session);
            var previous = await EffectiveAccountIdAsync(cancellationToken);
            await Accounts.Indexes.CreateOneAsync(new CreateIndexModel<BilibiliAccount>(Builders<BilibiliAccount>.IndexKeys.Ascending(x => x.Uid), new CreateIndexOptions { Unique = true }), cancellationToken: cancellationToken);
            var account = session.AccountId is { } accountId
                ? await RequireAccountAsync(accountId, cancellationToken)
                : await Accounts.Find(x => x.Uid == user.Uid).FirstOrDefaultAsync(cancellationToken) ?? new BilibiliAccount { Uid = user.Uid };
            if (account.Uid != user.Uid)
            {
                throw new InfrastructureException(MessageCode.BILIBILI_RELOGIN_IDENTITY_MISMATCH, 409);
            }
            account.UserName = user.UserName;
            if (!string.IsNullOrWhiteSpace(avatar))
            {
                account.AvatarUrl = avatar;
            }
            account.ProtectedCredentials = protector.Protect(JsonSerializer.Serialize(credentials));
            account.Status = "Valid";
            account.LastValidatedAtUtc = DateTime.UtcNow;
            await Accounts.ReplaceOneAsync(x => x.Id == account.Id, account, new ReplaceOptions { IsUpsert = session.AccountId is null }, cancellationToken);
            RotateLifetime(account.Id);
            await NotifyDefaultChangeAsync(previous, CancellationToken.None);
            return (await ListAccountsCoreAsync(cancellationToken)).Single(x => x.Id == account.Id);
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
        await accountGate.WaitAsync(cancellationToken);
        try
        {
            BilibiliAccount? account;
            if (accountId is not null)
            {
                account = await RequireAccountAsync(accountId, cancellationToken);
            }
            else
            {
                var accounts = await ReadAccountsAsync(cancellationToken);
                var selected = (await settingsStore.GetAsync(cancellationToken)).DefaultBilibiliAccountId;
                account = SelectDefaultAccount(accounts, selected);
            }
            if (account is { Status: "Expired" })
            {
                throw new InfrastructureException(MessageCode.BILIBILI_ACCOUNT_EXPIRED, 409);
            }
            return (account, account is null ? new() : DeserializeCredentials(account));
        }
        finally
        {
            accountGate.Release();
        }
    }

    private async Task<IReadOnlyList<BilibiliAccountSummary>> ListAccountsCoreAsync(CancellationToken cancellationToken)
    {
        var accounts = await ReadAccountsAsync(cancellationToken);
        var selected = (await settingsStore.GetAsync(cancellationToken)).DefaultBilibiliAccountId;
        var effective = SelectDefaultAccount(accounts, selected) is { Status: not "Expired" } usable ? usable.Id : null;
        return accounts.Select(account => Profile(account, selected, effective)).ToArray();
    }

    private Task<List<BilibiliAccount>> ReadAccountsAsync(CancellationToken cancellationToken) =>
        Accounts.Find(FilterDefinition<BilibiliAccount>.Empty).SortBy(x => x.CreatedAtUtc).ThenBy(x => x.Id).ToListAsync(cancellationToken);

    private async Task<BilibiliAccount> RequireAccountAsync(string id, CancellationToken cancellationToken) =>
        await Accounts.Find(x => x.Id == id).FirstOrDefaultAsync(cancellationToken)
        ?? throw new InfrastructureException(MessageCode.BILIBILI_ACCOUNT_NOT_FOUND, 409);

    private static BilibiliAccount? SelectDefaultAccount(IReadOnlyList<BilibiliAccount> accounts, string? selected) =>
        accounts.FirstOrDefault(x => x.Id == selected && x.Status == "Valid") ?? accounts.FirstOrDefault(x => x.Status == "Valid") ?? accounts.FirstOrDefault();

    private static void RequireValidAccount(BilibiliAccount account)
    {
        if (account.Status != "Valid")
        {
            throw new InfrastructureException(account.Status == "Expired" ? MessageCode.BILIBILI_ACCOUNT_EXPIRED : MessageCode.BILIBILI_ACCOUNT_UNAVAILABLE, 409);
        }
    }

    private async Task<string?> EffectiveAccountIdAsync(CancellationToken cancellationToken)
    {
        var accounts = await ReadAccountsAsync(cancellationToken);
        var selected = (await settingsStore.GetAsync(cancellationToken)).DefaultBilibiliAccountId;
        return SelectDefaultAccount(accounts, selected) is { Status: not "Expired" } usable ? usable.Id : null;
    }

    private async Task NotifyDefaultChangeAsync(string? previous, CancellationToken cancellationToken)
    {
        if (previous != await EffectiveAccountIdAsync(cancellationToken))
        {
            lock (lifetimeGate)
            {
                if (disposed != 0)
                {
                    return;
                }
                var retired = defaultLifetime;
                defaultLifetime = new();
                retiredLifetimes.Add(retired);
                retired.Cancel();
            }
        }
    }

    private async Task<bool> SaveValidationAsync(BilibiliAccount account, string status, CancellationToken cancellationToken)
    {
        await accountGate.WaitAsync(cancellationToken);
        try
        {
            var previous = await EffectiveAccountIdAsync(cancellationToken);
            var saved = await Accounts.FindOneAndUpdateAsync(x => x.Id == account.Id && x.ProtectedCredentials == account.ProtectedCredentials,
                Builders<BilibiliAccount>.Update.Set(x => x.Status, status).Set(x => x.LastValidatedAtUtc, DateTime.UtcNow),
                cancellationToken: cancellationToken);
            if (saved is null)
            {
                return false;
            }
            if (saved.Status != "Expired" && status == "Expired")
            {
                RotateLifetime(account.Id);
            }
            await NotifyDefaultChangeAsync(previous, CancellationToken.None);
            return true;
        }
        finally
        {
            accountGate.Release();
        }
    }

    private BilibiliLoginCredentials DeserializeCredentials(BilibiliAccount account) => JsonSerializer.Deserialize<BilibiliLoginCredentials>(protector.Unprotect(account.ProtectedCredentials)) ?? throw new InfrastructureException(MessageCode.BILIBILI_CREDENTIALS_CORRUPT, 503);

    private async Task<BilibiliBindingSession> CreateSessionAsync(string? accountId, CancellationToken cancellationToken)
    {
        await accountGate.WaitAsync(cancellationToken);
        try
        {
            PruneSessions();
            if (sessions.Count >= 50)
            {
                throw new InfrastructureException(MessageCode.BILIBILI_TOO_MANY_LOGIN_FLOWS, 429);
            }
            var account = accountId is null ? null : await RequireAccountAsync(accountId, cancellationToken);
            var session = new BilibiliBindingSession(new(httpClientFactory.CreateClient("bilibili-login"), true), account?.Id, account?.Uid);
            sessions[session.Id] = session;
            return session;
        }
        finally
        {
            accountGate.Release();
        }
    }

    private BilibiliBindingSession RequireSession(string id)
    {
        if (!sessions.TryGetValue(id, out var session) || session.ExpiresAtUtc <= DateTime.UtcNow)
        {
            throw new InfrastructureException(MessageCode.BILIBILI_LOGIN_EXPIRED, 410);
        }
        RequireAvailableSession(session);
        return session;
    }

    private static void RequireAvailableSession(BilibiliBindingSession session)
    {
        if (session.IsInvalidated)
        {
            throw new InfrastructureException(MessageCode.BILIBILI_ACCOUNT_NOT_FOUND, 409);
        }
        if (session.ExpiresAtUtc <= DateTime.UtcNow)
        {
            throw new InfrastructureException(MessageCode.BILIBILI_LOGIN_EXPIRED, 410);
        }
    }

    private void PruneSessions()
    {
        foreach (var session in sessions.Values.Where(x => x.ExpiresAtUtc <= DateTime.UtcNow))
        {
            if (session.Gate.Wait(0))
            {
                try
                {
                    RemoveSession(session.Id);
                }
                finally
                {
                    session.Gate.Release();
                }
            }
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
        lock (lifetimeGate)
        {
            if (disposed != 0)
            {
                return;
            }
            credentialLifetimes.TryGetValue(id, out var previous);
            credentialLifetimes[id] = new();
            if (previous is not null)
            {
                retiredLifetimes.Add(previous);
                previous.Cancel();
            }
        }
    }

    private CancellationToken GetLifetimeToken(string? accountId)
    {
        lock (lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            return accountId is null ? defaultLifetime.Token : credentialLifetimes.GetOrAdd(accountId, _ => new()).Token;
        }
    }

    private static BilibiliAccountSummary Profile(BilibiliAccount account, string? selected, string? effective) => new(account.Id, account.Uid, account.UserName, account.AvatarUrl, account.Status, account.LastValidatedAtUtc, account.Id == selected, account.Id == effective);
    private static BilibiliChallenge Challenge(BilibiliLoginChallenge challenge) => new(challenge.Type, challenge.Token, challenge.Gt, challenge.Challenge, challenge.ImageUrl);
    private static string NormalizeImage(string? value) => string.IsNullOrWhiteSpace(value) ? "" : value.StartsWith("//", StringComparison.Ordinal) ? "https:" + value : value.Replace("http://", "https://", StringComparison.OrdinalIgnoreCase);
}
