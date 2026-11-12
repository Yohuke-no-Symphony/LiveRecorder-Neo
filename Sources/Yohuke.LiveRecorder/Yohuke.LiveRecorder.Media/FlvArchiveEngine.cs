using System.IO.Pipelines;
using BililiveRecorder.Flv;
using BililiveRecorder.Flv.Grouping;
using BililiveRecorder.Flv.Parser;
using BililiveRecorder.Flv.Pipeline;
using BililiveRecorder.Flv.Pipeline.Actions;
using BililiveRecorder.Flv.Writer;
using Yohuke.LiveRecorder.Application.Media;

namespace Yohuke.LiveRecorder.Media;

internal static class FlvArchiveEngine
{
    internal static async Task<IReadOnlyList<FlvArchivedOutput>> RunAsync(Stream stream, RecordingRequest request, IProgress<MediaRecordingUpdate> progress, CancellationToken cancellationToken, Func<CancellationToken, Task>? capacityCheck = null, bool collectOutputs = true)
    {
        var memory = new DefaultMemoryStreamProvider();
        var observations = new Dictionary<long, FlvSourceTagInfo>();
        using var reader = new ObservedFlvReader(new FlvTagPipeReader(PipeReader.Create(stream), memory, skipData: false, leaveOpen: false, logger: null), observations);
        using var groups = new TagGroupReader(reader, leaveOpen: true);
        var target = new FlvArchiveTargetProvider(request, progress, collectOutputs);
        var tagWriter = new IndexedFlvTagWriter(new FlvTagFileWriter(target, memory, null), target, observations);
        using var writer = new FlvProcessingContextWriter(tagWriter, allowMissingHeader: true, disableKeyframes: true, logger: null);
        var pipeline = new ProcessingPipelineBuilder()
            .AddRule(new GopSizeSplitRule(request.MaxSegmentBytes, target))
            .AddDefaultRules()
            .AddRemoveFillerDataRule()
            .Build();
        var state = new Dictionary<object, object?>();
        var context = new FlvProcessingContext();
        try
        {
            while (await groups.ReadGroupAsync(cancellationToken) is { } group)
            {
                if (capacityCheck is not null)
                {
                    await capacityCheck(cancellationToken);
                }
                var lastIndex = group switch
                {
                    PipelineDataAction data => data.Tags.Max(tag => tag.Index),
                    PipelineHeaderAction header => header.AllTags.Max(tag => tag.Index),
                    PipelineScriptAction script => script.Tag?.Index ?? -1,
                    PipelineEndAction end => end.Tag.Index,
                    _ => -1
                };
                var originalIndices = observations.Keys.Where(index => index <= lastIndex).ToArray();
                context.Reset(group, state);
                pipeline(context);
                await writer.WriteAsync(context);
                await target.CommitAsync();
                foreach (var index in originalIndices)
                {
                    observations.Remove(index);
                }
                if (context.Actions.OfType<PipelineDisconnectAction>().FirstOrDefault() is { } disconnect)
                {
                    throw new IOException($"The source stream requires reconnection: {disconnect.Reason}");
                }
            }
        }
        finally
        {
            if (target.Current is not null)
            {
                await target.CommitAsync();
            }
            writer.Dispose();
        }
        return target.Outputs;
    }
}
