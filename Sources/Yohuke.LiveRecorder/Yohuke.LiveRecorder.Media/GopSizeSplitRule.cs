using BililiveRecorder.Flv;
using BililiveRecorder.Flv.Pipeline;
using BililiveRecorder.Flv.Pipeline.Actions;

namespace Yohuke.LiveRecorder.Media;

internal sealed class GopSizeSplitRule : ISimpleProcessingRule
{
    private readonly long maximumBytes;
    private readonly FlvArchiveTargetProvider target;

    internal GopSizeSplitRule(long maximumBytes, FlvArchiveTargetProvider target)
    {
        this.maximumBytes = maximumBytes;
        this.target = target;
    }

    public void Run(FlvProcessingContext context, Action next)
    {
        if (maximumBytes > 0 && target.Current?.CommittedBytes >= maximumBytes
            && context.Actions.OfType<PipelineDataAction>().Any(data => data.Tags.FirstOrDefault(tag => tag.Type == TagType.Video)?.IsKeyframeData() == true))
        {
            context.Actions.Insert(0, PipelineNewFileAction.Instance);
        }
        next();
    }
}
