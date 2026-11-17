using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Yohuke.LiveRecorder.AI.Worker.Storage;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.AI.Worker.Execution;

internal sealed class JournaledAiChatClient(IAiChatClient client, AiCheckpointStore store, AiExecutionLease lease, Action? checkCapacity = null) : IAiChatClient
{
    /// <inheritdoc />
    public async Task<AiCompletion> CompleteAsync(AiSettings settings, string apiKey, JsonArray messages,
        JsonArray tools, int maxOutputTokens, CancellationToken cancellationToken)
    {
        var snapshot = store.Read();
        var journal = snapshot.Journal ?? throw new InvalidOperationException("No durable AI request reservation exists.");
        if (!snapshot.Execution.RequestInFlight || journal.RequestNumber != snapshot.Execution.ModelRequests)
        {
            throw new AiExecutionPausedException("The AI request does not match its durable reservation.");
        }
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            snapshot.Assignment.JobId, snapshot.Assignment.Attempt, journal.RequestNumber,
            settings.Provider, settings.BaseUrl, settings.Model, settings.ThinkingMode, settings.ReasoningEffort,
            messages, tools, maxOutputTokens
        }, WorkerProtocol.JsonOptions)));
        if (journal.Dispatched)
        {
            if (!journal.Applied && journal.HasKnownResponse && journal.RequestFingerprint == fingerprint)
            {
                store.Update(value => value.Journal!.Returned = true);
                return KnownResponse(journal);
            }
            if (journal.HasKnownResponse && !journal.Applied && journal.RequestFingerprint != fingerprint)
            {
                store.Update(value => value.State = "Rejected");
            }
            throw new AiExecutionPausedException("A previously dispatched AI request cannot be repeated.");
        }
        lease.RequireModelCall();
        if (!journal.Confirmed)
        {
            throw new AiExecutionPausedException("The coordinator has not confirmed this AI request.");
        }
        checkCapacity?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        store.Update(value =>
        {
            value.Journal!.RequestFingerprint = fingerprint;
            value.Journal.Dispatched = true;
        });
        try
        {
            var response = await client.CompleteAsync(settings, apiKey, messages, tools, maxOutputTokens, cancellationToken);
            store.Update(value =>
            {
                value.Journal!.Completion = response;
                value.Journal.Returned = true;
            });
            return response;
        }
        catch (AiProviderException error)
        {
            store.Update(value =>
            {
                value.Journal!.Error = new()
                {
                    Code = error.Code, UnknownOutcome = error.UnknownOutcome, PromptTokens = error.PromptTokens,
                    CompletionTokens = error.CompletionTokens, Retryable = error.Retryable
                };
                value.Journal.Returned = true;
            });
            throw;
        }
    }

    private static AiCompletion KnownResponse(AiResponseJournal journal)
    {
        if (journal.Completion is { } response)
        {
            return response;
        }
        var error = journal.Error!;
        throw new AiProviderException(error.Code, error.UnknownOutcome, error.PromptTokens, error.CompletionTokens, error.Retryable);
    }
}
