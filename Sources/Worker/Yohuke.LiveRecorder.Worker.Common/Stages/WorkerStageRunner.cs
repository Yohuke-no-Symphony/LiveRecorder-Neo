namespace Yohuke.LiveRecorder.Worker.Common.Stages;

internal sealed class WorkerStageRunner<TContext>(IReadOnlyList<IWorkerStage<TContext>> stages) where TContext : WorkerStageContext
{
    internal async Task RunAsync(TContext context, CancellationToken cancellationToken)
    {
        if (stages.Count == 0 || stages.Select(value => value.Definition.Id).Distinct(StringComparer.Ordinal).Count() != stages.Count)
        {
            throw new InvalidOperationException("A pipeline requires uniquely identified stages.");
        }
        context.Initialize(stages.Select(value => value.Definition));
        for (var index = 0; index < stages.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stage = stages[index];
            if (context.Get(stage.Definition.Id).State == "Completed")
            {
                if (await stage.RestoreAsync(context, cancellationToken))
                {
                    context.Restored(stage.Definition);
                    continue;
                }
                context.InvalidateFrom(stage.Definition.Id);
            }
            context.Begin(stage.Definition);
            WorkerStageResult result;
            try
            {
                result = await stage.ExecuteAsync(context, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                context.End("Interrupted");
                throw;
            }
            catch (Exception error)
            {
                context.End("Failed", error.Message);
                throw;
            }
            context.End("Completed");
            if (result.RestartFromStageId is { } restart)
            {
                var restartIndex = stages.ToList().FindIndex(value => value.Definition.Id == restart);
                if (restartIndex < 0 || restartIndex > index)
                {
                    throw new InvalidOperationException("A stage may only restart at itself or an earlier stage.");
                }
                context.InvalidateFrom(restart);
                index = restartIndex - 1;
            }
        }
    }
}
