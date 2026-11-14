namespace Yohuke.LiveRecorder.Server.Bandwidth;

internal sealed class BandwidthQueue(bool downloads)
{
    private readonly Dictionary<string, BandwidthUserQueue> users = new(StringComparer.Ordinal);
    private readonly LinkedList<BandwidthUserQueue> active = new();

    internal void Enqueue(BandwidthRequest request)
    {
        var account = request.Transfer.Account;
        if (!users.TryGetValue(account.Id, out var user))
        {
            user = new(account);
            users.Add(account.Id, user);
        }
        request.Node = user.Requests.AddLast(request);
        user.Node ??= user.Deficit > 0 ? active.AddFirst(user) : active.AddLast(user);
    }

    internal BandwidthUserQueue? Candidate(int globalQuantum)
    {
        var count = active.Count;
        for (var index = 0; index < count; index++)
        {
            var user = active.First!.Value;
            if (!downloads || user.Account.DownloadBucket.Available > 0)
            {
                var quantum = downloads ? Math.Min(globalQuantum, user.Account.DownloadBucket.Quantum) : globalQuantum;
                user.Deficit = user.Deficit <= 0 ? quantum : Math.Min(quantum, user.Deficit);
                return user;
            }
            Rotate(user);
        }
        return null;
    }

    internal BandwidthRequest Take(BandwidthUserQueue user, int bytes)
    {
        var request = user.Requests.First!.Value;
        user.Deficit -= bytes;
        Remove(request);
        if (user.Node is not null && user.Deficit == 0)
        {
            Rotate(user);
        }
        return request;
    }

    internal void Remove(BandwidthRequest request)
    {
        if (request.Node is null)
        {
            return;
        }
        var user = users[request.Transfer.Account.Id];
        user.Requests.Remove(request.Node);
        request.Node = null;
        if (user.Requests.Count == 0 && user.Node is not null)
        {
            active.Remove(user.Node);
            user.Node = null;
        }
    }

    internal void Refund(BandwidthAccount account, int bytes, int quantum)
    {
        if (users.TryGetValue(account.Id, out var user))
        {
            user.Deficit = Math.Min(quantum, user.Deficit + bytes);
        }
    }

    internal void Forget(BandwidthAccount account) => users.Remove(account.Id);

    private void Rotate(BandwidthUserQueue user)
    {
        active.Remove(user.Node!);
        user.Node = active.AddLast(user);
    }
}
