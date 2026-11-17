using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Infrastructure.Auth;

internal static class LegacyPermissionMapping
{
    internal static string? GetId(string name) => name.ToLowerInvariant() switch
    {
        "managerecordings" => PermissionIds.MANAGE_RECORDINGS,
        "viewanalytics" => PermissionIds.VIEW_ANALYTICS,
        "purgerecordings" => PermissionIds.PURGE_RECORDINGS,
        _ => null
    };

    internal static PermissionSet Convert(IEnumerable<string> enabled) =>
        new(PermissionSet.MemberDefaults.Concat(enabled));
}
