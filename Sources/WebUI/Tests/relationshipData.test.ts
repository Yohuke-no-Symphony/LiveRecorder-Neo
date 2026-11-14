import { describe, expect, it } from 'vitest'
import { clampAnalyticsRange, onlineLineData, relationshipPoints } from '../src/components/analytics/relationshipData'
import type { AnalyticsBucket } from '../src/models/media'

function bucket(offsetMs: number, coverage = 60000, average: number | null = 100): AnalyticsBucket
{
  return { offsetMs, durationMs: 60000, onlineAverage: average, onlinePeak: average, onlineObservedMs: coverage, messages: 4, danmuCount: 3, superChatCount: 1, superChatAmount: 30, activeUsers: 2, danmuUsers: 1, payingUsers: 1, paidAmount: 30, giftAmount: 0, guardAmount: 0 }
}

describe('relationship chart data', () =>
{
  it('keeps zero observations and visibly separates missing intervals', () =>
  {
    expect(onlineLineData([{ fromMs: 0, toMs: 1000, count: 0 }, { fromMs: 2000, toMs: 3000, count: 20 }]))
      .toEqual([[0, 0], [1000, 0], [1000, null], [2000, 20], [3000, 20], [3000, null]])
  })
  it('rejects incomplete minutes and insufficient coverage without discarding real zero', () =>
  {
    const values = [bucket(0, 48000, 0), bucket(60000, 47999), bucket(120000, 60000, null), bucket(180000)]
    expect(relationshipPoints(values, 'superChatAmount', { fromMs: 0, toMs: 210000 })).toEqual([[0, 30, 0]])
  })
  it('bounds ranges and rejects reversed or non-finite input', () =>
  {
    expect(clampAnalyticsRange(-5, 500, 100)).toEqual({ fromMs: 0, toMs: 100 })
    expect(clampAnalyticsRange(50, 20, 100)).toBeUndefined()
    expect(clampAnalyticsRange(0, Number.NaN, 100)).toBeUndefined()
  })
})
