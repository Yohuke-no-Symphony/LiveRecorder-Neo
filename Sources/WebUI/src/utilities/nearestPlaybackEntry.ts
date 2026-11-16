export function findNearestPlaybackEntryIndex(entries: readonly { offsetMs: number }[], offsetMs: number, durationMs: number): number
{
  if (!Number.isFinite(offsetMs) || !Number.isFinite(durationMs) || durationMs <= 0)
  {
    return -1
  }
  let nearestIndex = -1
  let nearestDistance = Infinity
  for (let index = 0; index < entries.length; index++)
  {
    const entryOffset = entries[index]!.offsetMs
    if (!Number.isFinite(entryOffset) || entryOffset < 0 || entryOffset >= durationMs)
    {
      continue
    }
    const distance = Math.abs(entryOffset - offsetMs)
    if (nearestIndex < 0 || distance < nearestDistance || distance === nearestDistance && entryOffset < entries[nearestIndex]!.offsetMs)
    {
      nearestIndex = index
      nearestDistance = distance
    }
  }
  return nearestIndex
}
