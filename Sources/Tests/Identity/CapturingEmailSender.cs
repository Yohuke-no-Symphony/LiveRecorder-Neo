using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Yohuke.LiveRecorder.Application.Auth;

namespace Yohuke.LiveRecorder.Identity.Tests;

internal sealed class CapturingEmailSender : IEmailSender
{
    internal ConcurrentDictionary<string, string> Messages { get; } = new();
    public Task SendAsync(string email, string subject, string body, CancellationToken cancellationToken = default)
    {
        Messages[email] = body;
        return Task.CompletedTask;
    }
    internal string CodeFor(string email) => Regex.Match(Messages[email], @"\b[0-9]{6}\b").Value;
}
