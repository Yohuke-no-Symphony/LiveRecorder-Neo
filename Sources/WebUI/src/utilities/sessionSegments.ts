import type { SessionDetail, TimelineGap } from '@/models/media'

const SEGMENT_GAP_TOLERANCE_MS = 100

export function removeSessionSegment(detail: SessionDetail, segmentId: string): SessionDetail
{
  const segments = detail.segments.filter(segment => segment.id !== segmentId)
  const gaps: TimelineGap[] = []
  let durationMs = 0
  for (const segment of [...segments].filter(segment => segment.durationMs > 0).sort((left, right) => left.startOffsetMs - right.startOffsetMs))
  {
    if (segment.startOffsetMs > durationMs + SEGMENT_GAP_TOLERANCE_MS)
    {
      gaps.push({ startMs: durationMs, endMs: segment.startOffsetMs })
    }
    durationMs = Math.max(durationMs, segment.startOffsetMs + segment.durationMs)
  }
  return {
    ...detail,
    session: { ...detail.session, totalBytes: segments.reduce((sum, segment) => sum + segment.committedBytes, 0) },
    segments,
    durationMs,
    gaps,
  }
}

export function sessionMediaChanged(previous: SessionDetail, current: SessionDetail): boolean
{
  if (previous.session.mediaRevision !== current.session.mediaRevision)
  {
    return true
  }
  const previousIds = new Set(previous.segments.map(segment => segment.id))
  const currentIds = new Set(current.segments.map(segment => segment.id))
  return previous.segments.some(segment => !currentIds.has(segment.id))
    || (!!current.session.endedAtUtc && current.segments.some(segment => !previousIds.has(segment.id)))
}
