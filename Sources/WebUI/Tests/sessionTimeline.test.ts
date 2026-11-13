// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { defineComponent, h, ref } from 'vue'
import { i18n } from '../src/plugins/i18n'
import type { SessionTimeline } from '../src/models/sessionTimeline'
import { useSessionTimeline } from '../src/composables/useSessionTimeline'

const { timelineRequest } = vi.hoisted(() => ({ timelineRequest: vi.fn() }))
vi.mock('../src/apis/MediaAPI', () => ({ MediaAPI: { timeline: timelineRequest } }))
const mounted: VueWrapper[] = []
function snapshot(revision = 'r1'): SessionTimeline
{
  return { durationMs: 10000, bucketWidthMs: 1000, revision, heat: [{ offsetMs: 1000, count: 1 }], markers: [] }
}
function setupFeed(live = true)
{
  const id = ref('s1')
  const enabled = ref(true)
  const isLive = ref(live)
  let feed!: ReturnType<typeof useSessionTimeline>
  mounted.push(mount(defineComponent({ setup() { feed = useSessionTimeline(id, enabled, isLive); return () => h('div') } }), { global: { plugins: [i18n] } }))
  return { id, enabled, isLive, get feed() { return feed } }
}
beforeEach(() => { timelineRequest.mockReset(); vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] }) })
afterEach(() => { mounted.splice(0).forEach(wrapper => wrapper.unmount()); vi.useRealTimers() })

describe('录播时间轴刷新', () =>
{
  it('直播增量刷新，同版本快照保持引用稳定', async () =>
  {
    timelineRequest.mockImplementation(async () => snapshot())
    const state = setupFeed()
    await flushPromises()
    const first = state.feed.timeline.value
    await vi.advanceTimersByTimeAsync(10000)
    await flushPromises()
    expect(timelineRequest).toHaveBeenCalledTimes(2)
    expect(state.feed.timeline.value).toBe(first)
  })
  it('刷新失败保留上一份数据，重试可更新', async () =>
  {
    timelineRequest.mockResolvedValueOnce(snapshot())
    const state = setupFeed()
    await flushPromises()
    const first = state.feed.timeline.value
    timelineRequest.mockRejectedValueOnce(new Error('暂时离线'))
    await vi.advanceTimersByTimeAsync(10000)
    await flushPromises()
    expect(state.feed.timeline.value).toBe(first)
    expect(state.feed.error.value).toBe('暂时离线')
    timelineRequest.mockResolvedValueOnce(snapshot('r2'))
    await state.feed.refresh()
    expect(state.feed.timeline.value?.revision).toBe('r2')
    expect(state.feed.error.value).toBe('')
  })
  it('已结束录播只加载一次，直播结束时补取最后快照', async () =>
  {
    timelineRequest.mockImplementation(async () => snapshot())
    const state = setupFeed(false)
    await flushPromises()
    await vi.advanceTimersByTimeAsync(20000)
    expect(timelineRequest).toHaveBeenCalledTimes(1)
    state.isLive.value = true
    await flushPromises()
    const count = timelineRequest.mock.calls.length
    state.isLive.value = false
    await flushPromises()
    expect(timelineRequest.mock.calls.length).toBe(count + 1)
  })
  it('场次切换会取消旧请求并拒绝迟到响应', async () =>
  {
    let resolveOld!: (value: SessionTimeline) => void
    timelineRequest.mockImplementationOnce(() => new Promise<SessionTimeline>(resolve => { resolveOld = resolve }))
    const state = setupFeed()
    await flushPromises()
    const signal = timelineRequest.mock.calls[0]![2] as AbortSignal
    timelineRequest.mockResolvedValueOnce(snapshot('new-session'))
    state.id.value = 's2'
    await flushPromises()
    expect(signal.aborted).toBe(true)
    expect(state.feed.timeline.value?.revision).toBe('new-session')
    resolveOld(snapshot('old-session'))
    await flushPromises()
    expect(state.feed.timeline.value?.revision).toBe('new-session')
  })
})
