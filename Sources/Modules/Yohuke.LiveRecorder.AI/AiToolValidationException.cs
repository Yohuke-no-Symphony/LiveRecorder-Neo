using System.Text.Json.Nodes;

namespace Yohuke.LiveRecorder.AI;

internal sealed class AiToolValidationException(string field, string constraint, IEnumerable<string>? missingEvidenceIds = null) : Exception("Invalid tool arguments")
{
    internal JsonObject Feedback { get; } = new()
    {
        ["error"] = "Invalid tool arguments", ["type"] = "validation_error", ["field"] = field, ["constraint"] = constraint,
        ["missingRequiredEvidenceIds"] = missingEvidenceIds is null ? null : new JsonArray(missingEvidenceIds.Select(id => JsonValue.Create(id)).ToArray())
    };
}
