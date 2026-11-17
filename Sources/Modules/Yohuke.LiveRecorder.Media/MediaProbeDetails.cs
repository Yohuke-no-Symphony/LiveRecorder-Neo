using Yohuke.LiveRecorder.Application.Media;

namespace Yohuke.LiveRecorder.Media;

internal sealed record MediaProbeDetails(MediaProbe Probe, string CodecSignature);
