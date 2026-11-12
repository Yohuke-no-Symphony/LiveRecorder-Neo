using Yohuke.LiveRecorder.Application;

namespace Yohuke.LiveRecorder.Grouping.Tests;

internal sealed class GroupingSecretProtector : ISecretProtector
{
    public string Protect(string value) => value;
    public string Unprotect(string value) => value;
}
