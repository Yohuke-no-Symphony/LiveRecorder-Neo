using BililiveRecorder.Flv;
using BililiveRecorder.Flv.Pipeline;
using BililiveRecorder.Flv.Pipeline.Actions;

namespace Yohuke.LiveRecorder.Media;

internal sealed class GopSizeSplitRule : ISimpleProcessingRule
{
    private readonly long maximumBytes;
    private readonly FlvArchiveTargetProvider target;
    private readonly FlvSegmentTimeline timeline = new();

    internal GopSizeSplitRule(long maximumBytes, FlvArchiveTargetProvider target)
    {
        this.maximumBytes = maximumBytes;
        this.target = target;
    }

    public void Run(FlvProcessingContext context, Action next)
    {
        next();
        var automaticSplit = !context.Actions.OfType<PipelineNewFileAction>().Any();
        var output = new List<PipelineAction>();
        foreach (var action in context.Actions)
        {
            if (action is PipelineNewFileAction)
            {
                timeline.Reset();
            }
            else if (action is PipelineDataAction data)
            {
                var media = data.Tags.Where(tag => tag.IsData())
                    .Select(tag => (Tag: tag, Timestamp: timeline.Expand(tag.Timestamp))).ToArray();
                if (media.Length > 0)
                {
                    var origin = media.Min(value => value.Timestamp);
                    timeline.BeginFile(timeline.FileOriginMs ?? origin);
                    if (automaticSplit && maximumBytes > 0 && target.Current?.CommittedBytes >= maximumBytes
                        && data.Tags.FirstOrDefault(tag => tag.Type == TagType.Video && tag.IsData())?.IsKeyframeData() == true)
                    {
                        var presentationStart = media.Min(value => value.Timestamp + FlvTagTiming.CompositionOffsetMs(value.Tag)) - origin;
                        target.CloseAt(origin - timeline.FileOriginMs!.Value, presentationStart);
                        timeline.BeginFile(origin);
                        output.Add(PipelineNewFileAction.Instance);
                        automaticSplit = false;
                    }
                    foreach (var value in media)
                    {
                        value.Tag.Timestamp = timeline.ToFileTimestamp(value.Timestamp);
                    }
                }
            }
            else if (action is PipelineEndAction end && timeline.FileOriginMs is not null)
            {
                end.Tag.Timestamp = timeline.ToFileTimestamp(timeline.Expand(end.Tag.Timestamp));
            }
            output.Add(action);
        }
        context.Actions.Clear();
        context.Actions.AddRange(output);
    }
}
