namespace Yohuke.LiveRecorder.Clipping.Contracts;

/// <summary>Estimates temporary storage from assigned local ranges and bounded output headroom.</summary>
public static class ClipWorkspacePolicy
{
    private const long MIN_OUTPUT_RESERVE_BYTES = 64L * 1024 * 1024;

    /// <summary>Reserves raw ranges, reconstructed inputs and output space without counting unrelated source bytes.</summary>
    public static long RequiredBytes(ClipAssignment assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        var sourceBytes = 0L;
        foreach (var input in assignment.Inputs)
        {
            if (input.IsGap)
            {
                continue;
            }
            var range = input.Range;
            if (range is null || range.SourceLength <= 0 || range.StartOffset < 0 || range.EndOffset <= range.StartOffset
                || range.EndOffset > range.SourceLength)
            {
                throw new ArgumentException("A clipping workspace requires valid bounded input ranges.", nameof(assignment));
            }
            sourceBytes = checked(sourceBytes + range.EndOffset - range.StartOffset);
        }
        if (sourceBytes == 0)
        {
            throw new ArgumentException("A clipping workspace requires at least one media range.", nameof(assignment));
        }
        return checked(sourceBytes * 2 + Math.Max(MIN_OUTPUT_RESERVE_BYTES, checked(sourceBytes * 4)));
    }
}
