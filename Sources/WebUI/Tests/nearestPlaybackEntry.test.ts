import { describe, expect, it } from 'vitest'
import { findNearestPlaybackEntryIndex } from '../src/utilities/nearestPlaybackEntry'

describe('定位最近的播放条目', () =>
{
  it('按距离选择，等距选择更早的一条，同一时刻保留第一条', () =>
  {
    const entries = [1000, 3000, 3000, 8000].map(offsetMs => ({ offsetMs }))
    expect(findNearestPlaybackEntryIndex(entries, 7000, 10000)).toBe(3)
    expect(findNearestPlaybackEntryIndex(entries, 2000, 10000)).toBe(0)
    expect(findNearestPlaybackEntryIndex(entries, 3000, 10000)).toBe(1)
  })

  it('排除无法播放的条目，空列表和无效时间不能定位', () =>
  {
    const entries = [-1000, NaN, 1000, 10000, Infinity].map(offsetMs => ({ offsetMs }))
    expect(findNearestPlaybackEntryIndex(entries, 9900, 10000)).toBe(2)
    expect(findNearestPlaybackEntryIndex(entries, 0, 10000)).toBe(2)
    expect(findNearestPlaybackEntryIndex([], 0, 10000)).toBe(-1)
    expect(findNearestPlaybackEntryIndex(entries, NaN, 10000)).toBe(-1)
    expect(findNearestPlaybackEntryIndex(entries, 1000, Infinity)).toBe(-1)
    expect(findNearestPlaybackEntryIndex(entries, 0, 0)).toBe(-1)
  })
})
