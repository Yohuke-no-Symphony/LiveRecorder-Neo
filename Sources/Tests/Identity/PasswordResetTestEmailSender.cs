using System.Collections.Concurrent;
using Yohuke.LiveRecorder.Application.Auth;

namespace Yohuke.LiveRecorder.Identity.Tests;

internal sealed class PasswordResetTestEmailSender : IEmailSender
{
    private int attempts;
    internal ConcurrentQueue<(string Email, string Subject, string Body)> Messages { get; } = new();
    internal bool Fail { get; set; }
    internal int Attempts => attempts;

    /// <inheritdoc />
    public Task SendAsync(string email, string subject, string body, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref attempts);
        if (Fail)
        {
            throw new IOException("Simulated SMTP failure.");
        }
        Messages.Enqueue((email, subject, body));
        return Task.CompletedTask;
    }
}
