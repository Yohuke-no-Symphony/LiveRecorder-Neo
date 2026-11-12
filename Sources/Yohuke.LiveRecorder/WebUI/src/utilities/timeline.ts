import type { TimelineGap } from '../models/media'

export function clampOffset(value: number, durationMs: number): number
{
  return Math.max(0, Math.min(Number.isFinite(value) ? value : 0, Math.max(0, durationMs)))
}

export function findGap(offsetMs: number, gaps: readonly TimelineGap[]): TimelineGap | undefined
{
  return gaps.find(gap => gap.startMs <= offsetMs && offsetMs < gap.endMs)
}

export function windowPosition(offsetMs: number, windowStartMs: number): number
{
  return Math.max(0, offsetMs - windowStartMs) / 1000
}

export function playbackPosition(videoSeconds: number, windowStartMs: number, durationMs: number): number
{
  return clampOffset(windowStartMs + Math.max(0, videoSeconds) * 1000, durationMs)
}

export function parseTimecode(input: string): number | undefined
{
  const match = /^(?:(\d+):)?([0-5]?\d):([0-5]\d)(?:\.(\d{1,3}))?$/.exec(input.trim())
  if (!match)
  {
    return undefined
  }
  return (Number(match[1] ?? 0) * 3600 + Number(match[2]) * 60 + Number(match[3])) * 1000 + Number((match[4] ?? '').padEnd(3, '0'))
}

export function normalizeTimelineGaps(gaps: readonly TimelineGap[], durationMs: number): TimelineGap[]
{
  const result: TimelineGap[] = []
  for (const gap of [...gaps].sort((a, b) => a.startMs - b.startMs))
  {
    const startMs = clampOffset(gap.startMs, durationMs)
    const endMs = clampOffset(gap.endMs, durationMs)
    if (endMs <= startMs)
    {
      continue
    }
    const previous = result.at(-1)
    if (previous && startMs <= previous.endMs)
    {
      previous.endMs = Math.max(previous.endMs, endMs)
    }
    else
    {
      result.push({ startMs, endMs })
    }
  }
  return result
}
