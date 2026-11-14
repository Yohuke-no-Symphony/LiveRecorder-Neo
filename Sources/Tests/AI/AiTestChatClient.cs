using System.Text.Json.Nodes;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.AI.Tests;

internal sealed class AiTestChatClient(Func<int, JsonArray, AiCompletion> respond) : IAiChatClient
{
    internal int Calls { get; private set; }
    internal List<int> OutputLimits { get; } = [];

    public Task<AiCompletion> CompleteAsync(AiSettings settings, string apiKey, JsonArray messages, JsonArray tools, int maxOutputTokens, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        OutputLimits.Add(maxOutputTokens);
        return Task.FromResult(respond(Calls, messages));
    }
}
