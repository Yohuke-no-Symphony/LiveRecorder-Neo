// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { defineComponent, h, nextTick } from 'vue'
import { i18n } from '../src/plugins/i18n'
import type { RecordedEvent } from '../src/models/media'
import EventFeed from '../src/components/player/EventFeed.vue'

const { eventsRequest } = vi.hoisted(() => ({ eventsRequest: vi.fn() }))
vi.mock('../src/apis/MediaAPI', () => ({ MediaAPI: { events: eventsRequest } }))
const mounted: VueWrapper[] = []
const frames = new Map<number, FrameRequestCallback>()
const observers: { targets: Set<Element>; notify: () => void; disconnect: ReturnType<typeof vi.fn> }[] = []
const scrollToIndex = vi.fn()
let frameId = 0
const card = defineComponent({ setup: (_, { slots }) => () => h('div', slots.default?.()) })
const virtualScroll = defineComponent({
  props: ['items', 'itemKey'],
  setup(props, { slots, expose })
  {
    expose({ scrollToIndex })
    return () => h('div', (props.items as RecordedEvent[]).flatMap(item => slots.default?.({ item }) ?? []))
  },
})
function event(sequence: number, offsetMs: number): RecordedEvent
{
  return { schemaVersion: 1, sequence, eventId: `event-${sequence}`, kind: 'Danmu', command: 'DANMU_MSG', receivedAtUtc: '2026-11-13T00:00:00Z', offsetMs, userId: '', userName: '观众', text: `消息${sequence}`, quantity: 1 }
}
async function nextFrame(): Promise<void>
{
  const callbacks = [...frames.values()]
  frames.clear()
  for (const callback of callbacks)
  {
    callback(16)
  }
  await nextTick()
  await flushPromises()
}
async function setupFeed()
{
  eventsRequest.mockResolvedValue({ items: [event(1, 1000), event(2, 2500), event(3, 3000)], nextCursor: null })
  const wrapper = mount(EventFeed, { props: { sessionId: 's1', offsetMs: 2000, enabled: true, isLive: false }, global: { plugins: [i18n], stubs: { VCard: card, VVirtualScroll: virtualScroll, VBtn: true, VIcon: true, VChipGroup: true, VChip: true, VAlert: true, VProgressLinear: true } } })
  mounted.push(wrapper)
  await flushPromises()
  const viewport = wrapper.get('.event-scroll').element as HTMLElement
  let height = 1000
  let top = 700
  Object.defineProperties(viewport, {
    clientHeight: { configurable: true, get: () => 300 },
    scrollHeight: { configurable: true, get: () => height },
    scrollTop: { configurable: true, get: () => top, set: value => { top = Math.max(0, Math.min(Number(value), height - 300)) } },
  })
  await nextFrame()
  scrollToIndex.mockClear()
  return { wrapper, viewport, grow: (amount: number) => { height += amount }, gap: () => height - top - 300 }
}

beforeEach(() =>
{
  frames.clear()
  observers.length = 0
  frameId = 0
  scrollToIndex.mockReset()
  eventsRequest.mockReset()
  vi.stubGlobal('requestAnimationFrame', vi.fn((callback: FrameRequestCallback) => { const id = ++frameId; frames.set(id, callback); return id }))
  vi.stubGlobal('cancelAnimationFrame', vi.fn((id: number) => { frames.delete(id) }))
  vi.stubGlobal('ResizeObserver', vi.fn(function (callback: ResizeObserverCallback)
  {
    const targets = new Set<Element>()
    const disconnect = vi.fn(() => targets.clear())
    const observer = { observe: (target: Element) => targets.add(target), unobserve: (target: Element) => targets.delete(target), disconnect }
    observers.push({ targets, disconnect, notify: () => callback([], observer as ResizeObserver) })
    return observer
  }))
})
afterEach(() => { mounted.splice(0).forEach(wrapper => wrapper.unmount()); vi.unstubAllGlobals(); vi.restoreAllMocks() })

describe('直播消息按实际滚动范围贴底', () =>
{
  it('新消息的虚拟定位留下小幅余量时仍将末条贴到真实底部', async () =>
  {
    const state = await setupFeed()
    scrollToIndex.mockImplementation(() => { state.grow(80); state.viewport.scrollTop = state.viewport.scrollHeight - state.viewport.clientHeight - 18 })
    await state.wrapper.setProps({ offsetMs: 2500 })
    expect(state.gap()).toBe(18)
    await nextFrame()
    expect(state.gap()).toBe(0)
    expect(scrollToIndex).toHaveBeenCalledExactlyOnceWith(1, 'end')
    expect(frames.size).toBe(0)
  })
  it('尺寸通知当时已到底，下一帧发生的行高增长也会按最新尺寸校正', async () =>
  {
    const state = await setupFeed()
    expect(state.gap()).toBe(0)
    observers[0]!.notify()
    state.grow(24)
    await nextFrame()
    expect(state.gap()).toBe(0)
    expect(scrollToIndex).not.toHaveBeenCalled()
  })
  it('无需新的尺寸通知，跟随中的程序滚动偏移也会被纠正', async () =>
  {
    const state = await setupFeed()
    state.viewport.scrollTop -= 12
    await state.wrapper.get('.event-scroll').trigger('scroll')
    await nextFrame()
    expect(state.gap()).toBe(0)
    expect(scrollToIndex).not.toHaveBeenCalled()
  })
  it('消息突发与多次测量合并到一帧，以最新实际高度贴底', async () =>
  {
    const state = await setupFeed()
    scrollToIndex.mockImplementation(() => { state.grow(60) })
    await state.wrapper.setProps({ offsetMs: 2500 })
    observers[0]!.notify()
    await state.wrapper.setProps({ offsetMs: 3000 })
    observers[0]!.notify()
    expect(frames.size).toBe(1)
    await nextFrame()
    expect(state.gap()).toBe(0)
    expect(frames.size).toBe(0)
  })
  it('用户上翻后取消待执行贴底，之后的尺寸变化和程序滚动不打断阅读', async () =>
  {
    const state = await setupFeed()
    state.grow(20)
    observers[0]!.notify()
    await state.wrapper.get('.event-scroll').trigger('wheel', { deltaY: -100 })
    state.viewport.scrollTop = 400
    const top = state.viewport.scrollTop
    observers[0]!.notify()
    await state.wrapper.get('.event-scroll').trigger('scroll')
    await nextFrame()
    expect(state.viewport.scrollTop).toBe(top)
    expect(frames.size).toBe(0)
    expect(state.wrapper.get('.feed-heading v-btn-stub').attributes('color')).toBe('secondary')
  })
  it('卸载断开尺寸观察并取消已排队的贴底帧', async () =>
  {
    const state = await setupFeed()
    state.grow(20)
    observers[0]!.notify()
    state.wrapper.unmount()
    expect(observers[0]!.disconnect).toHaveBeenCalledTimes(1)
    expect(frames.size).toBe(0)
  })
})
