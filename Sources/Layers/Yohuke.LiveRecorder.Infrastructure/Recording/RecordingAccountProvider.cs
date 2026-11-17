using System.Security.Cryptography;
using System.Text.Json;
using MongoDB.Driver;
using Yohuke.DanmuCore.Models.Accounts;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Persistence;
using Yohuke.LiveRecorder.Infrastructure.Transcoding;
using Yohuke.LiveRecorder.Recording.Contracts;

namespace Yohuke.LiveRecorder.Infrastructure.Recording;

/// <summary>Builds minimal recording-only credential snapshots for authenticated remote capture nodes.</summary>
public sealed class RecordingAccountProvider(IMongoContext context, ISecretProtector protector, ISettingsStore settings)
{
    /// <summary>Returns a stable configuration fingerprint without placing credential contents in control messages.</summary>
    public async Task<string> FingerprintAsync(CancellationToken cancellationToken)
    {
        var accounts = await AccountsAsync(cancellationToken);
        return TranscodeAssetRegistry.Hash(string.Join('|', accounts.Select(value => value.Id + ":" + value.ProtectedCredentials)));
    }

    /// <summary>Decrypts valid capture cookies only while serving the owning Worker's assignment definition.</summary>
    public async Task<List<RecordingAccount>> ReadAsync(string? preferredId, CancellationToken cancellationToken)
    {
        var defaultId = (await settings.GetAsync(cancellationToken)).DefaultBilibiliAccountId;
        var accounts = await AccountsAsync(cancellationToken);
        var result = new List<RecordingAccount>();
        foreach (var account in accounts.OrderByDescending(value => value.Id == preferredId).ThenByDescending(value => value.Id == defaultId))
        {
            try
            {
                var credentials = JsonSerializer.Deserialize<BilibiliLoginCredentials>(protector.Unprotect(account.ProtectedCredentials));
                if (!string.IsNullOrWhiteSpace(credentials?.Cookie))
                {
                    result.Add(new() { AccountId = account.Id, Uid = account.Uid, Cookie = credentials.Cookie });
                }
            }
            catch (Exception error) when (error is JsonException or CryptographicException or FormatException)
            {
            }
        }
        return result;
    }

    private Task<List<BilibiliAccount>> AccountsAsync(CancellationToken cancellationToken) =>
        context.Collection<BilibiliAccount>("bilibili_accounts").Find(value => value.Status == "Valid")
            .SortBy(value => value.CreatedAtUtc).ToListAsync(cancellationToken);
}
