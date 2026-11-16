export function mergePlaybackEntries<T extends { offsetMs: number; sequence: number }>(previous: T[], additions: readonly T[]): T[]
{
  if (!additions.length)
  {
    return previous
  }
  const compare = (left: T, right: T) => left.offsetMs - right.offsetMs || left.sequence - right.sequence
  const incoming = [...additions].sort(compare)
  if (!previous.length)
  {
    return incoming
  }
  if (compare(previous.at(-1)!, incoming[0]!) <= 0)
  {
    return previous.concat(incoming)
  }
  const result: T[] = []
  let left = 0
  let right = 0
  while (left < previous.length && right < incoming.length)
  {
    result.push(compare(previous[left]!, incoming[right]!) <= 0 ? previous[left++]! : incoming[right++]!)
  }
  while (left < previous.length)
  {
    result.push(previous[left++]!)
  }
  while (right < incoming.length)
  {
    result.push(incoming[right++]!)
  }
  return result
}
