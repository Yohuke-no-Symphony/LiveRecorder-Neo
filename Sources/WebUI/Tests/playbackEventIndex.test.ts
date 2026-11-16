import { describe, expect, it } from 'vitest'
import { ref, shallowRef } from 'vue'
import { usePlaybackEvents } from '../src/composables/usePlaybackEvents'
import type { RecordedEvent } from '../src/models/media'
import { displayEvents } from '../src/utilities/eventPresentation'

function event(sequence: number, patch: Partial<RecordedEvent> = {}): RecordedEvent
{
  return { schemaVersion: 1, sequence, eventId: `${sequence}`, kind: 'Danmu', command: '', receivedAtUtc: '', offsetMs: sequence * 1000, userId: '', userName: '', text: `${sequence}`, quantity: 1, ...patch }
}

describe('全量弹幕的本地时间索引', () =>
{
  it('大归档推进一条消息时不重新扫描已播放的五万条历史', () =>
  {
    let reads = 0
    const events = Array.from({ length: 50000 }, (_, index) =>
    {
      const item = event(index + 1)
      Object.defineProperty(item, 'offsetMs', { get: () => { reads++; return (index + 1) * 1000 } })
      return item
    })
    const offset = ref(49999000)
    const visible = usePlaybackEvents(shallowRef(events), offset, 'all', 500)
    expect(visible.value).toHaveLength(500)
    reads = 0
    offset.value = 50000000
    expect(visible.value.at(-1)?.sequence).toBe(50000)
    expect(reads).toBeLessThan(100)
  })

  it('红包状态保留较早奖品，回退时间不会泄露未来开奖结果', () =>
  {
    const events = [
      event(1, { kind: 'RedPocket', transactionId: 'lot', payload: { redPocket: { hasDetails: true, awards: [{ giftId: 'gift', name: '奖品' }], hasResult: false } } }),
      ...Array.from({ length: 500 }, (_, index) => event(index + 2)),
      event(502, { kind: 'RedPocket', transactionId: 'lot', payload: { redPocket: { hasResult: true, winners: [{ uid: 1, userName: '获奖者', giftId: 'gift' }] } } }),
    ]
    const offset = ref(502000)
    const visible = usePlaybackEvents(shallowRef(events), offset, 'all', 2)
    expect(visible.value).toEqual(displayEvents(events, offset.value).slice(-2))
    expect(JSON.stringify(visible.value)).toContain('奖品')
    offset.value = 1000
    expect(visible.value).toEqual(displayEvents(events, offset.value).slice(-2))
    expect(JSON.stringify(visible.value)).not.toContain('获奖者')
  })
})
