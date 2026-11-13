import type { ClipSelection } from '../models/clipSelection'
import { clampOffset } from './timeline'

const DEFAULT_CLIP_DURATION_MS = 30000

export function resolveClipRange(selection: ClipSelection | undefined, currentMs: number, durationMs: number): [number, number]
{
  if (selection?.startMs !== undefined)
  {
    const startMs = clampOffset(selection.startMs, durationMs)
    return [startMs, selection.endMs === undefined ? Math.min(durationMs, startMs + DEFAULT_CLIP_DURATION_MS) : clampOffset(selection.endMs, durationMs)]
  }
  if (selection?.endMs !== undefined)
  {
    const endMs = clampOffset(selection.endMs, durationMs)
    return [Math.max(0, endMs - DEFAULT_CLIP_DURATION_MS), endMs]
  }
  const startMs = Math.min(clampOffset(currentMs, durationMs), Math.max(0, durationMs - DEFAULT_CLIP_DURATION_MS))
  return [startMs, Math.min(durationMs, startMs + DEFAULT_CLIP_DURATION_MS)]
}
