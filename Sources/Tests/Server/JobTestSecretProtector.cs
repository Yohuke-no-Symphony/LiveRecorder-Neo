using Yohuke.LiveRecorder.Application;

namespace Yohuke.LiveRecorder.Tests;

internal sealed class JobTestSecretProtector : ISecretProtector
{
    public string Protect(string plaintext) => plaintext;
    public string Unprotect(string protectedValue) => protectedValue;
}
