using Yohuke.LiveRecorder.Clipping.Contracts;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Infrastructure.Clipping;

/// <summary>Combines the portable assignment with source identities retained only by the server.</summary>
public sealed record ClipJobPlan(ClipAssignment Assignment, List<ClipSourceSnapshot> Sources);
