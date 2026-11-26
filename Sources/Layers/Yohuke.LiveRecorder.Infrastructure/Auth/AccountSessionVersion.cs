using MongoDB.Driver;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Infrastructure.Auth;

internal static class AccountSessionVersion
{
    internal static FilterDefinition<UserAccount> Filter(long version)
    {
        var filter = Builders<UserAccount>.Filter;
        var match = filter.Eq(user => user.SessionVersion, version);
        return version == 0 ? match | filter.Exists(user => user.SessionVersion, false) : match;
    }
}
