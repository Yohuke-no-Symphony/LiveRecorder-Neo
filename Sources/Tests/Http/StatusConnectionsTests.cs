using Yohuke.LiveRecorder.Server.Realtime;
using Xunit;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>会话撤销只断开受影响的旧连接。</summary>
public sealed class StatusConnectionsTests
{
    /// <summary>迟到的撤销通知不能关闭安全操作之后建立的连接。</summary>
    [Fact]
    public void RevocationPreservesNewerAndOtherUsersConnections()
    {
        var connections = new StatusConnections();
        var aborted = new List<string>();
        connections.Add(new("old", "user", 0, () => aborted.Add("old")));
        connections.Add(new("new", "user", 1, () => aborted.Add("new")));
        connections.Add(new("other", "other-user", 0, () => aborted.Add("other")));
        connections.Revoke("user", 1);
        connections.Revoke("user", 1);
        Assert.Equal(["old"], aborted);
        Assert.Equal(["new", "other"], connections.All.Select(connection => connection.Id).Order().ToArray());
    }
}
