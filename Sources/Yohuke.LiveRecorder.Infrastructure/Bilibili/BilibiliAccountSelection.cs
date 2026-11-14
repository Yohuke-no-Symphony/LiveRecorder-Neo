using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Infrastructure.Bilibili;

internal sealed class BilibiliAccountSelection
{
    internal IReadOnlyList<BilibiliAccount> Candidates { get; }
    internal bool AllowsAnonymous { get; }
    internal MessageCode UnavailableCode { get; }

    internal BilibiliAccountSelection(IReadOnlyList<BilibiliAccount> accounts, string? preferredId, string? defaultId)
    {
        Candidates = accounts.Where(account => account.Status == "Valid")
            .OrderBy(account => account.Id == preferredId ? 0 : account.Id == defaultId ? 1 : 2)
            .ThenBy(account => account.CreatedAtUtc)
            .ThenBy(account => account.Id, StringComparer.Ordinal)
            .ToArray();
        AllowsAnonymous = accounts.Count == 0 && preferredId is null;
        UnavailableCode = accounts.Count == 0 && preferredId is not null
            ? MessageCode.BILIBILI_ACCOUNT_NOT_FOUND
            : accounts.Count > 0 && accounts.All(account => account.Status == "Expired")
                ? MessageCode.BILIBILI_ACCOUNT_EXPIRED
                : MessageCode.BILIBILI_ACCOUNT_UNAVAILABLE;
    }
}
