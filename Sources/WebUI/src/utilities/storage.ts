import type { StorageVolume } from '@/models/media'

export type StorageCategory = 'recordings' | 'clips' | 'trash' | 'other' | 'free'

export interface StorageSegment
{
  id: StorageCategory
  bytes: number
  percent: number
}

function nonnegative(value: number): number
{
  return Number.isFinite(value) ? Math.max(0, value) : 0
}

export function storageSegments(volume: StorageVolume): StorageSegment[]
{
  const total = nonnegative(volume.totalBytes)
  const free = Math.min(total, nonnegative(volume.freeBytes))
  let remaining = total - free
  const categories: [StorageCategory, number][] = [
    ['recordings', volume.recordingBytes], ['clips', volume.clipBytes], ['trash', volume.trashBytes],
  ]
  const segments = categories.map(([id, value]) =>
  {
    const bytes = nonnegative(value)
    const visibleBytes = Math.min(remaining, bytes)
    remaining -= visibleBytes
    return { id, bytes, percent: total > 0 ? visibleBytes / total * 100 : 0 }
  })
  return [
    ...segments,
    { id: 'other', bytes: nonnegative(volume.otherBytes), percent: total > 0 ? remaining / total * 100 : 0 },
    { id: 'free', bytes: free, percent: total > 0 ? free / total * 100 : 0 },
  ]
}
