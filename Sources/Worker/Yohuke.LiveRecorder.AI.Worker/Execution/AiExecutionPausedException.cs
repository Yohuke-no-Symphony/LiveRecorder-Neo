namespace Yohuke.LiveRecorder.AI.Worker.Execution;

internal sealed class AiExecutionPausedException(string message = "AI execution requires coordinator confirmation.") : IOException(message);
