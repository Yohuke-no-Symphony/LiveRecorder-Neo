import { effectScope, nextTick, ref, type EffectScope } from 'vue'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { BroadcastSession } from '../src/models/media'
import { useSessionPages } from '../src/composables/useSessionPages'

const { fetchSessions } = vi.hoisted(() => ({ fetchSessions: vi.fn() }))
vi.mock('../src/apis/MediaAPI', () => ({ MediaAPI: { sessions: fetchSessions } }))
vi.mock('../src/composables/usePolling', () => ({ usePolling: vi.fn() }))
let scope: EffectScope | undefined

function session(id: number, title = `录播 ${id}`): BroadcastSession
{
  return {
    id: String(id), createdAtUtc: '2026-11-12T00:00:00Z', streamerId: 'streamer', roomId: 1,
    title, coverUrl: '', startedAtUtc: '2026-11-12T00:00:00Z', lastMediaAtUtc: '2026-11-12T01:00:00Z',
    state: 'Completed', hasDanmu: true, totalBytes: 1024, isTrashed: false, isPermanent: false, mediaRevision: 1,
  }
}

function page(first: number, count = 50): BroadcastSession[]
{
  return Array.from({ length: count }, (_, index) => session(first - index))
}

function start(parameters = ref<{ streamerId?: string; search?: string }>({}))
{
  scope = effectScope()
  return scope.run(() => useSessionPages(() => parameters.value))!
}

beforeEach(() => { fetchSessions.mockReset() })
afterEach(() => { scope?.stop() })

describe('录播服务端分页', () =>
{
  it('使用服务端最后一条 ID 加载下一页并去重', async () =>
  {
    fetchSessions.mockResolvedValueOnce(page(100)).mockResolvedValueOnce([session(51), ...page(50, 4)])
    const state = start()
    await vi.waitFor(() => expect(state.loading.value).toBe(false))
    await state.loadMore()
    expect(fetchSessions.mock.calls[1]).toEqual([undefined, undefined, '51', 50])
    expect(state.sessions.value).toHaveLength(54)
    expect(state.sessions.value.at(-1)?.id).toBe('47')
    expect(state.hasMore.value).toBe(false)
  })

  it('轮询更新头页，保留已加载历史并更新相同 ID 的状态', async () =>
  {
    fetchSessions.mockResolvedValueOnce(page(100)).mockResolvedValueOnce(page(50)).mockResolvedValueOnce([session(101), session(100, '更新后的标题'), ...page(99, 48)])
    const state = start()
    await vi.waitFor(() => expect(state.loading.value).toBe(false))
    await state.loadMore()
    await state.refresh()
    expect(state.sessions.value).toHaveLength(101)
    expect(state.sessions.value.at(-1)?.id).toBe('1')
    expect(state.sessions.value.find(item => item.id === '100')?.title).toBe('更新后的标题')
    expect(state.hasMore.value).toBe(true)
  })

  it('超过一页新录播时继续拉取，直到与现有历史重合', async () =>
  {
    fetchSessions.mockResolvedValueOnce(page(100)).mockResolvedValueOnce(page(180)).mockResolvedValueOnce(page(130))
    const state = start()
    await vi.waitFor(() => expect(state.loading.value).toBe(false))
    await state.refresh()
    expect(fetchSessions.mock.calls[2]).toEqual([undefined, undefined, '131', 50])
    expect(state.sessions.value).toHaveLength(130)
    expect(state.sessions.value[0]?.id).toBe('180')
    expect(state.sessions.value.at(-1)?.id).toBe('51')
    expect(new Set(state.sessions.value.map(item => item.id)).size).toBe(130)
  })

  it('搜索提交服务器并重置历史游标，旧请求不能覆盖新搜索', async () =>
  {
    let completeOld: ((value: BroadcastSession[]) => void) | undefined
    fetchSessions.mockReturnValueOnce(new Promise<BroadcastSession[]>(resolve => { completeOld = resolve })).mockResolvedValueOnce([session(7, '目标标题')])
    const parameters = ref<{ streamerId?: string; search?: string }>({ streamerId: 'streamer' })
    const state = start(parameters)
    parameters.value = { streamerId: 'streamer', search: '目标' }
    await nextTick()
    await vi.waitFor(() => expect(state.loading.value).toBe(false))
    completeOld?.(page(100))
    await nextTick()
    expect(fetchSessions.mock.calls[1]).toEqual(['streamer', '目标', undefined, 50])
    expect(state.sessions.value.map(item => item.id)).toEqual(['7'])
    expect(state.hasMore.value).toBe(false)
  })

  it('加载更多失败保留当前列表并允许重试', async () =>
  {
    fetchSessions.mockResolvedValueOnce(page(100)).mockRejectedValueOnce(new Error('连接中断')).mockResolvedValueOnce(page(50, 1))
    const state = start()
    await vi.waitFor(() => expect(state.loading.value).toBe(false))
    await state.loadMore()
    expect(state.sessions.value).toHaveLength(50)
    expect(state.error.value).toBe('连接中断')
    expect(state.hasMore.value).toBe(true)
    await state.loadMore()
    expect(state.sessions.value).toHaveLength(51)
    expect(fetchSessions.mock.calls[2]?.[2]).toBe('51')
    expect(state.error.value).toBe('')
  })

  it('删除成功立即移除，删除前已发出的轮询响应不能重新加入该项', async () =>
  {
    let finishRefresh: ((value: BroadcastSession[]) => void) | undefined
    fetchSessions.mockResolvedValueOnce(page(100)).mockReturnValueOnce(new Promise<BroadcastSession[]>(resolve => { finishRefresh = resolve }))
    const state = start()
    await vi.waitFor(() => expect(state.loading.value).toBe(false))
    const refreshing = state.refresh()
    state.removeSessions(['99'])
    expect(state.sessions.value.some(item => item.id === '99')).toBe(false)
    finishRefresh?.(page(100))
    await refreshing
    expect(state.sessions.value.some(item => item.id === '99')).toBe(false)
    expect(state.sessions.value).toHaveLength(49)
  })

  it('删除当前分页游标项不丢失下一页位置，重新加载可显示恢复的录播', async () =>
  {
    fetchSessions.mockResolvedValueOnce(page(100)).mockResolvedValueOnce(page(50, 1)).mockResolvedValueOnce(page(100))
    const state = start()
    await vi.waitFor(() => expect(state.loading.value).toBe(false))
    state.removeSessions(['51'])
    await state.loadMore()
    expect(fetchSessions.mock.calls[1]).toEqual([undefined, undefined, '51', 50])
    expect(state.sessions.value.at(-1)?.id).toBe('50')
    await state.reload()
    expect(state.sessions.value.some(item => item.id === '51')).toBe(true)
  })
})
