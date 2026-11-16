// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { defineComponent, h, nextTick, reactive, ref } from 'vue'
import { ApiError } from '../src/apis/APIBase'
import { useSessionEventSnapshot } from '../src/composables/useSessionEventSnapshot'
import type { SessionEventSnapshot } from '../src/models/sessionEventSnapshot'
import type { RecordedEvent } from '../src/models/media'
import { i18n } from '../src/plugins/i18n'

const { read, saved, writes, remove } = vi.hoisted(() => ({ read: vi.fn(), saved: new Map<string, unknown>(), writes: vi.fn(), remove: vi.fn() }))
const auth = reactive({ user: { id: 'viewer', role: 'User', status: 'Active', permissions: ['recordings.view'] }, canViewRecordings: true })
vi.mock('../src/apis/MediaAPI', () => ({ MediaAPI: { eventSnapshot: read } }))
vi.mock('../src/stores/auth', () => ({ useAuthStore: () => auth }))
vi.mock('../src/utilities/previewBrowserCache', () => ({
  previewCacheKey: (...parts: string[]) => JSON.stringify(parts),
  readPreviewCache: async (key: string) => saved.get(key),
  writePreviewCache: async (key: string, value: unknown) => { writes(key, value); saved.set(key, value) },
  removePreviewCache: async (key: string) => { remove(key); saved.delete(key) },
}))
const mounted: VueWrapper[] = []

function event(sequence: number, offsetMs = sequence * 1000): RecordedEvent
{
  return { schemaVersion: 1, sequence, eventId: `${sequence}`, kind: 'Danmu', command: 'DANMU_MSG', receivedAtUtc: '', offsetMs, userId: 'u1', userName: '译者', text: `同传${sequence}`, quantity: 1 }
}

function snapshot(items: RecordedEvent[] = [event(1)], values: Partial<SessionEventSnapshot> = {}): SessionEventSnapshot
{
  return { version: 'v1', reset: true, items, translationSequences: items.map(item => item.sequence), cursor: 'cursor-1', isLive: false, ...values }
}

function deferred<T>()
{
  let resolve!: (value: T) => void
  let reject!: (value: unknown) => void
  const promise = new Promise<T>((accept, fail) => { resolve = accept; reject = fail })
  return { promise, resolve, reject }
}

function setupFeed(liveInitially = false, activeInitially = true)
{
  const id = ref('session-1')
  const active = ref(activeInitially)
  const enabled = ref(true)
  const live = ref(liveInitially)
  const revision = ref(1)
  let state!: ReturnType<typeof useSessionEventSnapshot>
  const wrapper = mount(defineComponent({ setup()
  {
    state = useSessionEventSnapshot({ sessionId: id, active, enabled, isLive: live, configurationRevision: revision })
    return () => h('div')
  } }), { global: { plugins: [i18n] } })
  mounted.push(wrapper)
  return { id, active, enabled, live, revision, wrapper, get state() { return state } }
}

beforeEach(() =>
{
  vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
  read.mockReset().mockResolvedValue(snapshot())
  writes.mockReset()
  remove.mockReset()
  saved.clear()
  auth.user = { id: 'viewer', role: 'User', status: 'Active', permissions: ['recordings.view'] }
  auth.canViewRecordings = true
  i18n.global.locale.value = 'zh-CN'
})
afterEach(() =>
{
  mounted.splice(0).forEach(wrapper => wrapper.unmount())
  vi.useRealTimers()
  vi.restoreAllMocks()
})

describe('完整播放事件的本地快照', () =>
{
  it('乱序新增只归并增量，重复游标页不替换已有对象或触发列表重建', async () =>
  {
    read.mockResolvedValueOnce(snapshot([event(1, 1000), event(3, 9000)]))
    const context = setupFeed()
    await flushPromises()
    read.mockResolvedValueOnce(snapshot([event(2, 4000), event(4, 500)], { reset: false, cursor: 'cursor-2' }))
    await context.state.refresh()
    expect(context.state.events.value.map(item => item.sequence)).toEqual([4, 1, 2, 3])
    expect(context.state.translations.value.map(item => item.sequence)).toEqual([4, 1, 2, 3])
    const previous = context.state.events.value
    read.mockResolvedValueOnce(snapshot([event(2, 4000)], { reset: false, cursor: 'cursor-2' }))
    await context.state.refresh()
    expect(context.state.events.value).toBe(previous)
  })

  it('同传索引失败不阻断普通弹幕，空增量保留错误，完整重试后恢复', async () =>
  {
    read.mockResolvedValueOnce(snapshot([event(1)], { translationError: '同传匹配超时', translationSequences: [] }))
    const context = setupFeed()
    await flushPromises()
    expect(context.state.events.value).toHaveLength(1)
    expect(context.state.error.value).toBe('')
    expect(context.state.translationError.value).toBe('同传匹配超时')
    read.mockResolvedValueOnce(snapshot([], { reset: false, translationSequences: [], translationError: '同传匹配超时' }))
    await context.state.refresh()
    expect(context.state.translationError.value).toBe('同传匹配超时')
    read.mockResolvedValueOnce(snapshot())
    await context.state.refresh(true)
    expect(read.mock.calls[2]?.[1]).toBeUndefined()
    expect(context.state.translationError.value).toBe('')
    expect(context.state.translations.value).toHaveLength(1)
  })

  it('一次加载完整归档和服务端同传索引，切换及结束场次的轮询不再请求', async () =>
  {
    read.mockResolvedValue(snapshot([event(3, 90000), event(1), event(2)], { translationSequences: [2] }))
    const context = setupFeed()
    await flushPromises()
    expect(context.state.events.value.map(item => item.sequence)).toEqual([1, 2, 3])
    expect(context.state.translations.value.map(item => item.sequence)).toEqual([2])
    context.active.value = false
    await nextTick()
    context.active.value = true
    await vi.advanceTimersByTimeAsync(60000)
    expect(read).toHaveBeenCalledTimes(1)
    expect(writes).toHaveBeenCalledTimes(1)
  })

  it('重新进入先验证持久快照游标，验证成功后复用已存正文', async () =>
  {
    const previous = setupFeed()
    await flushPromises()
    previous.wrapper.unmount()
    read.mockResolvedValue(snapshot([], { reset: false, translationSequences: [] }))
    const context = setupFeed()
    expect(context.state.events.value).toEqual([])
    await flushPromises()
    expect(read.mock.calls[1]?.[1]).toBe('cursor-1')
    expect(context.state.events.value.map(item => item.sequence)).toEqual([1])
    expect(context.state.hasSnapshot.value).toBe(true)
  })

  it('隐藏时完成在途完整下载，快速切回也不会重新下载', async () =>
  {
    const pending = deferred<SessionEventSnapshot>()
    read.mockReturnValueOnce(pending.promise)
    const context = setupFeed()
    await flushPromises()
    context.active.value = false
    await nextTick()
    expect(read.mock.calls[0]?.[2].aborted).toBe(false)
    context.active.value = true
    await nextTick()
    expect(read).toHaveBeenCalledTimes(1)
    pending.resolve(snapshot())
    await flushPromises()
    expect(context.state.events.value).toHaveLength(1)
  })

  it('录制中只用游标追加，保留未来时间和低序号事件，结束时补最终增量', async () =>
  {
    read.mockResolvedValueOnce(snapshot([event(2, 1000)], { isLive: true }))
    const context = setupFeed(true)
    await flushPromises()
    read.mockResolvedValueOnce(snapshot([event(1, 200000)], { reset: false, cursor: 'cursor-2', isLive: true }))
    await vi.advanceTimersByTimeAsync(10000)
    expect(read.mock.calls[1]?.[1]).toBe('cursor-1')
    expect(context.state.events.value.map(item => item.sequence)).toEqual([2, 1])
    read.mockResolvedValueOnce(snapshot([event(3, 220000)], { reset: false, cursor: 'cursor-3' }))
    context.live.value = false
    await flushPromises()
    expect(read.mock.calls[2]?.[1]).toBe('cursor-2')
    expect(context.state.events.value.map(item => item.sequence)).toEqual([2, 1, 3])
    await vi.advanceTimersByTimeAsync(30000)
    expect(read).toHaveBeenCalledTimes(3)
  })

  it('同传规则或源版本重置时整体替换，隐藏期间规则更新保留到激活', async () =>
  {
    const context = setupFeed()
    await flushPromises()
    context.active.value = false
    await nextTick()
    context.revision.value++
    await nextTick()
    expect(read).toHaveBeenCalledTimes(1)
    read.mockResolvedValue(snapshot([event(3)], { version: 'v2', cursor: 'cursor-new' }))
    context.active.value = true
    await flushPromises()
    expect(context.state.events.value.map(item => item.sequence)).toEqual([3])
    expect(context.state.translations.value.map(item => item.sequence)).toEqual([3])
    expect(read).toHaveBeenCalledTimes(2)
  })

  it('身份变化会取消旧请求并隔离缓存，迟到结果不可恢复旧账号正文', async () =>
  {
    const pending = deferred<SessionEventSnapshot>()
    read.mockReturnValueOnce(pending.promise)
    const context = setupFeed()
    await flushPromises()
    const signal = read.mock.calls[0]?.[2] as AbortSignal
    auth.user.id = 'other-viewer'
    read.mockResolvedValue(snapshot([event(2)]))
    await flushPromises()
    expect(signal.aborted).toBe(true)
    pending.resolve(snapshot([event(1)]))
    await flushPromises()
    expect(context.state.events.value.map(item => item.sequence)).toEqual([2])
    expect(writes).toHaveBeenCalledTimes(1)
  })

  it('权限失败清除当前快照及其持久缓存，普通网络失败保留上次完整数据', async () =>
  {
    const context = setupFeed()
    await flushPromises()
    read.mockRejectedValueOnce(new Error('网络不可用'))
    await context.state.refresh()
    expect(context.state.events.value).toHaveLength(1)
    read.mockRejectedValueOnce(new ApiError(403, '无权限'))
    await context.state.refresh()
    expect(context.state.events.value).toEqual([])
    expect(context.state.hasSnapshot.value).toBe(false)
    expect(remove).toHaveBeenCalledTimes(1)
  })
})
