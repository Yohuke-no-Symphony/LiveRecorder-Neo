import { effectScope, nextTick, ref, type EffectScope } from 'vue'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { BilibiliRoomSearchPage, BilibiliRoomSearchResult } from '../src/models/bilibili'
import { useLiveRoomSearch } from '../src/composables/useLiveRoomSearch'

const { searchRooms } = vi.hoisted(() => ({ searchRooms: vi.fn() }))
vi.mock('../src/apis/AuthAPI', () => ({ AuthAPI: { searchBilibiliRooms: searchRooms } }))
let scope: EffectScope | undefined

function room(roomId: string): BilibiliRoomSearchResult
{
  return { roomId, shortRoomId: '', ownerUid: '42', ownerName: '主播', title: '直播标题', isLive: true, sources: ['nickname'] }
}

function page(ids: string[], hasMore = false, current = 1): BilibiliRoomSearchPage
{
  return { results: ids.map(room), issues: [], page: current, hasMore }
}

function start(account = ref<string | null>('account-1'))
{
  scope = effectScope()
  return scope.run(() => useLiveRoomSearch(() => account.value))!
}

beforeEach(() => { searchRooms.mockReset() })
afterEach(() => { scope?.stop() })

describe('直播间搜索状态', () =>
{
  it('打开不发送请求，空关键词按所选账号查询关注直播间', async () =>
  {
    const state = start()
    expect(searchRooms).not.toHaveBeenCalled()
    searchRooms.mockResolvedValueOnce(page(['100']))
    await state.search()
    expect(searchRooms.mock.calls[0]![0]).toEqual({ keyword: '', page: 1, accountId: 'account-1' })
    expect(searchRooms.mock.calls[0]![1]).toBeInstanceOf(AbortSignal)
    expect(state.results.value.map(item => item.roomId)).toEqual(['100'])
    expect(state.searched.value).toBe(true)
  })

  it('分页沿用已提交关键词并按规范房号去重', async () =>
  {
    searchRooms.mockResolvedValueOnce(page(['100', '101', '100'], true)).mockResolvedValueOnce(page(['101', '102'], false, 2))
    const state = start()
    state.keyword.value = ' 主播 '
    await state.search()
    state.keyword.value = '尚未提交的草稿'
    await state.loadMore()
    expect(searchRooms.mock.calls[1]![0]).toEqual({ keyword: '主播', page: 2, accountId: 'account-1' })
    expect(state.results.value.map(item => item.roomId)).toEqual(['100', '101', '102'])
    expect(state.hasMore.value).toBe(false)
  })

  it('新搜索取消上一请求，迟到响应不能覆盖新结果', async () =>
  {
    let finishOld!: (value: BilibiliRoomSearchPage) => void
    searchRooms.mockReturnValueOnce(new Promise<BilibiliRoomSearchPage>(resolve => { finishOld = resolve })).mockResolvedValueOnce(page(['200']))
    const state = start()
    state.keyword.value = '旧搜索'
    const old = state.search()
    const oldSignal = searchRooms.mock.calls[0]![1] as AbortSignal
    state.keyword.value = '新搜索'
    await state.search()
    expect(oldSignal.aborted).toBe(true)
    finishOld(page(['100'], true))
    await old
    expect(state.results.value.map(item => item.roomId)).toEqual(['200'])
    expect(state.loading.value).toBe(false)
    expect(state.hasMore.value).toBe(false)
  })

  it('切换账号清空旧结果并取消请求，保留输入草稿', async () =>
  {
    let finish!: (value: BilibiliRoomSearchPage) => void
    searchRooms.mockReturnValueOnce(new Promise<BilibiliRoomSearchPage>(resolve => { finish = resolve }))
    const account = ref<string | null>('account-1')
    const state = start(account)
    state.keyword.value = '主播'
    const pending = state.search()
    const signal = searchRooms.mock.calls[0]![1] as AbortSignal
    account.value = 'account-2'
    await nextTick()
    expect(signal.aborted).toBe(true)
    finish(page(['100']))
    await pending
    expect(state.results.value).toEqual([])
    expect(state.loading.value).toBe(false)
    expect(state.searched.value).toBe(false)
    expect(state.keyword.value).toBe('主播')
    expect(searchRooms).toHaveBeenCalledTimes(1)
  })

  it('关闭作用域后取消请求并忽略不遵守取消的返回', async () =>
  {
    let finish!: (value: BilibiliRoomSearchPage) => void
    searchRooms.mockReturnValueOnce(new Promise<BilibiliRoomSearchPage>(resolve => { finish = resolve }))
    const state = start()
    const pending = state.search()
    const signal = searchRooms.mock.calls[0]![1] as AbortSignal
    scope!.stop()
    expect(signal.aborted).toBe(true)
    finish(page(['100']))
    await pending
    expect(state.results.value).toEqual([])
    expect(state.error.value).toBe('')
  })

  it('分页失败保留结果和页码，重试相同页且不重复发起分页', async () =>
  {
    searchRooms.mockResolvedValueOnce(page(['100'], true)).mockRejectedValueOnce(new Error('连接中断')).mockResolvedValueOnce(page(['101'], false, 2))
    const state = start()
    await state.search()
    await state.loadMore()
    expect(state.results.value.map(item => item.roomId)).toEqual(['100'])
    expect(state.error.value).toBe('连接中断')
    expect(state.hasMore.value).toBe(true)
    await Promise.all([state.loadMore(), state.loadMore()])
    expect(searchRooms).toHaveBeenCalledTimes(3)
    expect(searchRooms.mock.calls[2]![0].page).toBe(2)
    expect(state.results.value.map(item => item.roomId)).toEqual(['100', '101'])
    expect(state.error.value).toBe('')
  })
})
