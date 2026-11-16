// @vitest-environment jsdom
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest'
import { config, flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { defineComponent, h, isProxy, reactive } from 'vue'
import { i18n } from '../src/plugins/i18n'
import { ApiError } from '../src/apis/APIBase'
import type { RecordedEvent } from '../src/models/media'
import { displayEvents, redPocketData, safeImageUrl } from '../src/utilities/eventPresentation'
import DanmuContent from '../src/components/events/DanmuContent.vue'
import ScContent from '../src/components/events/ScContent.vue'
import GiftContent from '../src/components/events/GiftContent.vue'
import GuardContent from '../src/components/events/GuardContent.vue'
import RedPocketContent from '../src/components/events/RedPocketContent.vue'
import EventFeed from '../src/components/player/EventFeed.vue'
import MediaPlayer from '../src/components/player/MediaPlayer.vue'

const { hlsConstructor, supported, eventsRequest } = vi.hoisted(() => ({ hlsConstructor: vi.fn(), supported: vi.fn(() => true), eventsRequest: vi.fn() }))
vi.mock('hls.js', () => ({ default: Object.assign(hlsConstructor, { isSupported: supported, Events: { MANIFEST_PARSED: 'manifest', ERROR: 'error' } }) }))
const auth = reactive({ user: { id: 'viewer' }, canViewRecordings: true })
vi.mock('../src/stores/auth', () => ({ useAuthStore: () => auth }))
vi.mock('../src/apis/MediaAPI', () => ({ MediaAPI: {
  eventSnapshot: async (id: string, cursor?: string, signal?: AbortSignal) =>
  {
    const page = await eventsRequest(id, { cursor }, signal)
    return { version: 'v1', reset: !cursor, items: page.items, translationSequences: [], cursor: 'cursor-1', isLive: false }
  },
} }))
const mounted: VueWrapper[] = []
const originalPlugins = [...config.global.plugins]
const scrollToIndex = vi.fn()
const card = defineComponent({ setup: (_, { slots }) => () => h('div', slots.default?.()) })
const virtualScroll = defineComponent({
  props: ['items', 'itemKey'],
  setup(props, { slots, expose })
  {
    expose({ scrollToIndex })
    return () => h('div', (props.items as RecordedEvent[]).flatMap(item => slots.default?.({ item }) ?? []))
  },
})

function event(patch: Partial<RecordedEvent> = {}): RecordedEvent
{
  return { schemaVersion: 1, sequence: 1, eventId: 'event-1', kind: 'Danmu', command: 'DANMU_MSG', receivedAtUtc: '2026-11-12T00:00:01Z', offsetMs: 1000, userId: '1', userName: '用户甲', text: '', quantity: 1, ...patch }
}

function pocketEvents(): RecordedEvent[]
{
  return [
    event({ kind: 'RedPocket', transactionId: 'lot-100', text: '一起看直播', payload: { isDrawn: false, redPocket: { lotId: 100, danmu: '一起看直播', endTime: 1794412860, totalAwardCount: 2, hasDetails: true, hasResult: false, awards: [{ giftId: 'gift-1', name: '小电视', quantity: 2, imageUrl: 'https://cdn.example.test/gift.png' }], winners: [] } } }),
    event({ kind: 'RedPocket', sequence: 2, eventId: 'drawn', transactionId: 'lot-100', offsetMs: 2000, userId: '', userName: '', payload: { isDrawn: true, redPocket: { lotId: 100, endTime: 0, hasDetails: false, hasResult: false, awards: [], winners: [] } } }),
    event({ kind: 'RedPocket', sequence: 3, eventId: 'winners', transactionId: 'lot-100', offsetMs: 3000, payload: { isDrawn: true, redPocket: { lotId: 100, hasDetails: false, hasResult: true, awards: [], winners: [{ uid: 20, userName: '中奖用户', giftId: 'gift-1', giftName: '小电视', quantity: 1 }] } } }),
  ]
}

beforeAll(() => { config.global.plugins = [...originalPlugins, i18n] })
afterAll(() => { config.global.plugins = originalPlugins })
beforeEach(() => { auth.user.id = 'viewer'; auth.canViewRecordings = true; vi.stubGlobal('requestAnimationFrame', vi.fn(() => 1)); vi.stubGlobal('cancelAnimationFrame', vi.fn()); vi.stubGlobal('ResizeObserver', vi.fn(function () { return { observe: vi.fn(), unobserve: vi.fn(), disconnect: vi.fn() } })); i18n.global.locale.value = 'zh-CN'; hlsConstructor.mockClear(); supported.mockClear(); eventsRequest.mockReset(); scrollToIndex.mockClear() })
afterEach(() => { mounted.splice(0).forEach(wrapper => wrapper.unmount()); vi.restoreAllMocks(); vi.unstubAllGlobals(); vi.useRealTimers() })

describe('实际消息内容组件', () =>
{
  it('弹幕正文使用纯文本、行内表情和安全回复，不重复身份徽章', () =>
  {
    const wrapper = mount(DanmuContent, { props: { sessionId: 's1', event: event({ payload: { content: '<img src=x onerror=alert(1)> [笑] [坏]', replyUserName: '<b>用户乙</b>', isRoomAdmin: true, roomGuardLevel: 2, medalName: '粉丝牌', medalLevel: 12, emoticons: [{ text: '[笑]', url: 'https://cdn.example.test/smile.png', width: 24, height: 24 }, { text: '[坏]', url: 'javascript:alert(1)' }] } }) } })
    mounted.push(wrapper)
    expect(wrapper.text()).toContain('<img src=x onerror=alert(1)>')
    expect(wrapper.text()).toContain('回复 <b>用户乙</b>：')
    expect(wrapper.find('.message-badges').exists()).toBe(false)
    expect(wrapper.findAll('img')).toHaveLength(1)
    expect(new URL(wrapper.get('img').attributes('src')!).pathname).toBe('/api/sessions/s1/events/1/emoticons/0')
    expect(wrapper.find('[onerror]').exists()).toBe(false)
    expect(wrapper.text()).toContain('[坏]')
    expect(safeImageUrl('data:image/svg+xml,<svg onload=alert(1)>')).toBeUndefined()
  })

  it('SC 单独呈现金额及安全正文', () =>
  {
    const wrapper = mount(ScContent, { props: { event: event({ kind: 'SuperChat', amount: 30, text: '<script>test()</script>' }) } })
    mounted.push(wrapper)
    expect(wrapper.get('.sc-price').text()).toContain('30.00')
    expect(wrapper.get('.sc-text').text()).toBe('<script>test()</script>')
    expect(wrapper.find('script').exists()).toBe(false)
  })

  it.each([true, false])('礼物数量只显示一次，包括历史文本回退（有结构化名称：%s）', structured =>
  {
    const wrapper = mount(GiftContent, { props: { event: event({ kind: 'Gift', quantity: 3, text: '小花花 ×3', payload: structured ? { giftName: '小花花' } : null }) } })
    mounted.push(wrapper)
    expect(wrapper.text().match(/×3/g)).toHaveLength(1)
    expect(wrapper.text()).toContain('小花花')
  })

  it('大航海显示档位、数量，仅参考金额附加标注', async () =>
  {
    const value = event({ kind: 'Guard', quantity: 2, amount: 1380, priceSource: 'ProtocolQuote', payload: { guardLevel: 2 } })
    const wrapper = mount(GuardContent, { props: { event: value } })
    mounted.push(wrapper)
    expect(wrapper.text()).toContain('提督')
    expect(wrapper.text()).toContain('×2')
    expect(wrapper.text()).toContain('参考金额')
    await wrapper.setProps({ event: { ...value, priceSource: 'ProtocolPaid' } })
    expect(wrapper.text()).not.toContain('参考金额')
  })

  it('红包真实组件在开奖前不泄漏未来中奖者，开奖后保留已知奖品与用户名', async () =>
  {
    const source = pocketEvents()
    const initial = displayEvents(source, 1500)[0]!
    const wrapper = mount(RedPocketContent, { props: { event: initial } })
    mounted.push(wrapper)
    expect(wrapper.text()).toContain('待开奖')
    expect(wrapper.text()).toContain('小电视')
    expect(wrapper.text()).not.toContain('中奖用户')
    await wrapper.setProps({ event: displayEvents(source, 2500)[0]! })
    expect(wrapper.text()).toContain('已开奖，暂无结果')
    expect(wrapper.text()).not.toContain('中奖用户')
    await wrapper.setProps({ event: displayEvents(source, 3500)[0]! })
    expect(wrapper.text()).toContain('中奖用户')
    expect(wrapper.findAll('.pocket-award')).toHaveLength(1)
    expect(wrapper.findAll('.pocket-winner')).toHaveLength(1)
  })

  it('电池红包展示总额和每人的实际中奖数，奖品名称不会遮住数值或重复乘数量', async () =>
  {
    const source = [
      event({ kind: 'RedPocket', transactionId: 'battery-pocket', payload: { redPocket: { totalBattery: 12.5, totalAwardCount: 2, awards: [{ giftId: 'battery', name: '电池' }] } } }),
      event({ kind: 'RedPocket', transactionId: 'battery-pocket', sequence: 2, offsetMs: 2000, payload: { isDrawn: true, redPocket: { hasResult: true, totalBattery: null, awards: [], winners: [
        { uid: 20, userName: '用户乙', giftId: 'battery', giftName: '电池', batteryAmount: 10, quantity: 10 },
        { uid: 30, userName: '用户丙', giftId: 'battery', batteryAmount: 2.5, quantity: 1 },
      ] } } }),
    ]
    const wrapper = mount(RedPocketContent, { props: { event: displayEvents(source, 1500)[0]! } })
    mounted.push(wrapper)
    expect(wrapper.get('.pocket-meta').text()).toContain('共 12.5 电池')
    expect(wrapper.get('.pocket-meta').text()).toContain('2 份')
    expect(wrapper.find('.pocket-winner').exists()).toBe(false)

    await wrapper.setProps({ event: displayEvents(source, 2500)[0]! })
    expect(wrapper.get('.pocket-meta').text()).toContain('共 12.5 电池')
    expect(wrapper.findAll('.pocket-winner').map(row => row.element.lastElementChild?.textContent)).toEqual(['10 电池', '2.5 电池'])
  })

  it.each([
    ['zh-CN', '共 0 电池', '0 电池'],
    ['en-US', '0 batteries total', '0 batteries'],
    ['ja-JP', '電池 合計 0 個', '電池 0 個'],
  ] as const)('电池总额独立显示且零值和中奖数遵循当前语言（%s）', (locale, totalText, winnerText) =>
  {
    i18n.global.locale.value = locale
    const wrapper = mount(RedPocketContent, { props: { event: event({ kind: 'RedPocket', payload: { redPocket: { hasResult: true, totalBattery: 0, winners: [
      { uid: 20, userName: '中奖用户', giftName: '电池', batteryAmount: 0 },
    ] } } }) } })
    mounted.push(wrapper)
    expect(wrapper.get('.pocket-meta').text()).toBe(totalText)
    expect(wrapper.get('.pocket-winner').element.lastElementChild?.textContent).toBe(winnerText)
  })

  it('缺少电池金额时保留奖品名，亲密度和普通礼物分别显示实际数值与数量', () =>
  {
    const wrapper = mount(RedPocketContent, { props: { event: event({ kind: 'RedPocket', payload: { redPocket: { hasResult: true, totalBattery: null, totalPrice: 1000, awards: [{ giftId: 'gift', name: '小电视' }], winners: [
      { uid: 20, userName: '用户乙', giftName: '电池', batteryAmount: null, quantity: 1 },
      { uid: 30, userName: '用户丙', giftName: '亲密度', intimacyAmount: 25, quantity: 25 },
      { uid: 40, userName: '用户丁', giftId: 'gift', quantity: 2 },
    ] } } }) } })
    mounted.push(wrapper)
    expect(wrapper.find('.pocket-meta').exists()).toBe(false)
    expect(wrapper.findAll('.pocket-winner').map(row => row.element.lastElementChild?.textContent)).toEqual(['电池', '25 亲密度', '小电视 ×2'])
  })
})

describe('红包聚合和默认消息过滤', () =>
{
  it('按时间过滤后聚合同一 lotId，回退播放不会残留未来开奖结果', () =>
  {
    const source = pocketEvents().reverse()
    const completed = displayEvents(source, 3500)
    expect(completed).toHaveLength(1)
    expect(redPocketData(completed[0]!).hasResult).toBe(true)
    const before = displayEvents(source, 1500)
    expect(before).toHaveLength(1)
    expect(redPocketData(before[0]!).hasResult).toBe(false)
    expect(redPocketData(before[0]!).winners).toEqual([])
    expect((source[0]!.payload!.redPocket as Record<string, unknown>).winners).toHaveLength(1)
  })

  it('迟到的缺资料更新不能清空已知结果，重复结果不重复中奖者', () =>
  {
    const source = pocketEvents()
    source.push({ ...source[2]!, sequence: 4, offsetMs: 4000 })
    source.push({ ...source[1]!, sequence: 5, offsetMs: 5000 })
    const result = displayEvents(source, 5500)
    expect(result).toHaveLength(1)
    const data = redPocketData(result[0]!)
    expect(data.hasResult).toBe(true)
    expect(data.awards).toHaveLength(1)
    expect(data.winners).toHaveLength(1)
    expect(data.endTime).toBe(1794412860)
  })

  it('全部仅显示五类消息及Gap，不呈现Raw/RoomState，分类筛选保持准确', () =>
  {
    const events = ['Raw', 'RoomState', 'Danmu', 'SuperChat', 'Gift', 'Guard', 'RedPocket', 'Gap'].map((kind, index) => event({ kind, sequence: index + 1 }))
    expect(displayEvents(events, 2000).map(item => item.kind)).toEqual(['Danmu', 'SuperChat', 'Gift', 'Guard', 'RedPocket', 'Gap'])
    expect(displayEvents(events, 2000, 'Gift').map(item => item.kind)).toEqual(['Gift'])
  })

  it('实际EventFeed不渲染未知协议访客行，Gap不带访客身份', async () =>
  {
    eventsRequest.mockResolvedValue({ items: [event({ kind: 'Raw', text: '协议原文' }), event({ kind: 'RoomState', sequence: 2, text: '房间状态更新' }), event({ sequence: 3, text: '正常弹幕' }), event({ kind: 'Gift', sequence: 4, quantity: 3, text: '小花花 ×3', payload: { giftName: '小花花' } }), event({ kind: 'Gap', sequence: 5, text: '录制中断' })], nextCursor: null })
    const wrapper = mount(EventFeed, { props: { sessionId: 's1', offsetMs: 2000, enabled: true, isLive: false }, global: { stubs: { VCard: card, VVirtualScroll: virtualScroll, VBtn: true, VIcon: true, VAvatar: true, VImg: true, VChipGroup: true, VChip: true, VAlert: true, VProgressLinear: true } } })
    mounted.push(wrapper)
    await flushPromises()
    expect(wrapper.findAll('.event-row')).toHaveLength(3)
    expect(wrapper.text()).not.toContain('协议原文')
    expect(wrapper.text()).not.toContain('房间状态更新')
    expect(wrapper.text()).toContain('正常弹幕')
    expect(wrapper.text().match(/×3/g)).toHaveLength(1)
    expect(wrapper.get('.event-gap').find('v-avatar-stub').exists()).toBe(false)
  })
})

describe('空播放地址', () =>
{
  it('非gap窗口缺少URL时显示错误，不创建HLS播放器', async () =>
  {
    vi.spyOn(HTMLMediaElement.prototype, 'pause').mockImplementation(() => {})
    vi.stubGlobal('requestAnimationFrame', vi.fn(() => 1))
    vi.stubGlobal('cancelAnimationFrame', vi.fn())
    const wrapper = mount(MediaPlayer, { props: { modelValue: 0, durationMs: 10000, gaps: [], isLive: false, loadWindow: vi.fn().mockResolvedValue({ url: null, startOffsetMs: 0, endOffsetMs: 10000, requestedOffsetMs: 0, isGap: false, durationMs: 10000 }) }, global: { stubs: { VBtn: true, VSlider: true, VMenu: true, VList: true, VListItem: true, VIcon: true, VSpacer: true, VProgressCircular: true } } })
    mounted.push(wrapper)
    await flushPromises()
    expect(wrapper.get('.error-overlay').text()).toContain('当前时段没有可播放的视频')
    expect(hlsConstructor).not.toHaveBeenCalled()
  })
})

describe('消息播放与阅读稳定性', () =>
{
  it('暂停阅读后服务器撤销权限，刷新失败也会立即清除保留的旧正文', async () =>
  {
    eventsRequest.mockResolvedValueOnce({ items: [event({ text: '保留的私有正文' })], nextCursor: null })
    const wrapper = mountFeed()
    await flushPromises()
    await wrapper.get('.event-scroll').trigger('wheel', { deltaY: -100 })
    eventsRequest.mockRejectedValueOnce(new ApiError(403, '观看权限已撤销'))
    await wrapper.get('.feed-refresh').trigger('click')
    await flushPromises()
    expect(wrapper.text()).not.toContain('保留的私有正文')
    expect(wrapper.text()).toContain('观看权限已撤销')
  })

  it.each(['account', 'permission'] as const)('暂停阅读时变更%s也不能残留旧身份消息', async change =>
  {
    eventsRequest.mockResolvedValueOnce({ items: [event({ text: '旧账号的消息' })], nextCursor: null })
    const wrapper = mountFeed()
    await flushPromises()
    await wrapper.get('.event-scroll').trigger('wheel', { deltaY: -100 })
    eventsRequest.mockReturnValueOnce(new Promise(() => {}))
    if (change === 'account')
    {
      auth.user.id = 'other-viewer'
    }
    else
    {
      auth.canViewRecordings = false
    }
    await flushPromises()
    expect(wrapper.text()).not.toContain('旧账号的消息')
    expect(wrapper.find('.event-row').exists()).toBe(false)
  })

  function mountFeed(offsetMs = 2000, isLive = false): VueWrapper
  {
    const wrapper = mount(EventFeed, { props: { sessionId: 's1', offsetMs, enabled: true, isLive }, global: { stubs: { VCard: card, VVirtualScroll: virtualScroll, VBtn: true, VIcon: true, VAvatar: true, VImg: true, VChipGroup: true, VChip: true, VAlert: card, VProgressLinear: true } } })
    mounted.push(wrapper)
    return wrapper
  }

  it('慢请求跨过多个播放区间时继续读取，返回后结束加载', async () =>
  {
    let complete!: (value: { items: RecordedEvent[]; nextCursor: null }) => void
    eventsRequest.mockImplementationOnce(() => new Promise(resolve => { complete = resolve }))
    const wrapper = mountFeed()
    await flushPromises()
    const signal = eventsRequest.mock.calls[0]![2] as AbortSignal

    for (const offsetMs of [7000, 12000, 17000])
    {
      await wrapper.setProps({ offsetMs })
    }
    expect(eventsRequest).toHaveBeenCalledTimes(1)
    expect(signal.aborted).toBe(false)
    expect(wrapper.find('v-progress-linear-stub').exists()).toBe(true)

    complete({ items: [event({ text: '慢请求完成' })], nextCursor: null })
    await flushPromises()
    expect(wrapper.text()).toContain('慢请求完成')
    expect(wrapper.find('v-progress-linear-stub').exists()).toBe(false)
    expect(eventsRequest).toHaveBeenCalledTimes(1)
  })

  it('完整下载期间播放推进很远，完成后直接显示本地目标位置', async () =>
  {
    let complete!: (value: { items: RecordedEvent[]; nextCursor: null }) => void
    eventsRequest.mockImplementationOnce(() => new Promise(resolve => { complete = resolve }))
    const wrapper = mountFeed()
    await flushPromises()

    for (const offsetMs of [7000, 25000, 45000])
    {
      await wrapper.setProps({ offsetMs })
    }
    complete({ items: [event({ text: '先前位置消息' }), event({ sequence: 2, offsetMs: 44000, text: '最新位置消息' })], nextCursor: null })
    await flushPromises()

    expect(eventsRequest).toHaveBeenCalledTimes(1)
    expect(wrapper.text()).toContain('最新位置消息')
    expect(wrapper.find('v-progress-linear-stub').exists()).toBe(false)
  })

  it('直播通过游标轮询增量，播放推进不打断正在进行的追加', async () =>
  {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
    eventsRequest.mockResolvedValueOnce({ items: [event({ text: '已加载消息' })], nextCursor: null })
    const wrapper = mountFeed(2000, true)
    await flushPromises()
    let complete!: (value: { items: RecordedEvent[]; nextCursor: null }) => void
    eventsRequest.mockImplementationOnce(() => new Promise(resolve => { complete = resolve }))

    await wrapper.setProps({ offsetMs: 25000 })
    expect(eventsRequest).toHaveBeenCalledTimes(1)
    await vi.advanceTimersByTimeAsync(10000)
    const signal = eventsRequest.mock.calls[1]![2] as AbortSignal
    await wrapper.setProps({ offsetMs: 30000 })
    await vi.advanceTimersByTimeAsync(10000)
    expect(eventsRequest).toHaveBeenCalledTimes(2)
    expect(signal.aborted).toBe(false)

    complete({ items: [event({ sequence: 2, offsetMs: 29000, text: '预取完成' })], nextCursor: null })
    await flushPromises()
    expect(wrapper.text()).toContain('预取完成')
    expect(wrapper.find('v-progress-linear-stub').exists()).toBe(false)
    expect(eventsRequest).toHaveBeenCalledTimes(2)
  })

  it('慢请求失败时显示错误，播放推进不会触发立即重试循环', async () =>
  {
    let fail!: (error: Error) => void
    eventsRequest.mockImplementationOnce(() => new Promise((_resolve, reject) => { fail = reject }))
    const wrapper = mountFeed()
    await flushPromises()
    await wrapper.setProps({ offsetMs: 45000 })

    fail(new Error('弹幕读取失败'))
    await flushPromises()
    expect(eventsRequest).toHaveBeenCalledTimes(1)
    expect(wrapper.text()).toContain('弹幕读取失败')
    expect(wrapper.find('v-progress-linear-stub').exists()).toBe(false)
  })

  it('显式跳转复用完整下载，完成时按最新播放时间显示', async () =>
  {
    let complete!: (value: { items: RecordedEvent[]; nextCursor: null }) => void
    eventsRequest.mockImplementationOnce(() => new Promise(resolve => { complete = resolve }))
    const wrapper = mountFeed()
    await flushPromises()
    const signal = eventsRequest.mock.calls[0]![2] as AbortSignal

    await wrapper.setProps({ offsetMs: 45000, navigationRevision: 1 })
    await flushPromises()
    expect(signal.aborted).toBe(false)
    expect(eventsRequest).toHaveBeenCalledTimes(1)

    complete({ items: [event({ text: '历史消息' }), event({ sequence: 2, offsetMs: 44000, text: '跳转目标消息' })], nextCursor: null })
    await flushPromises()
    expect(wrapper.text()).toContain('跳转目标消息')
    expect(wrapper.text()).toContain('历史消息')
    expect(eventsRequest).toHaveBeenCalledTimes(1)
  })

  it('归档消息和协议资料保持普通对象，列表无需逐层创建响应式代理', async () =>
  {
    const payload = { content: '普通弹幕', emoticons: [{ text: '[笑]', url: 'https://example.test/smile.png' }] }
    const message = event({ payload })
    eventsRequest.mockResolvedValue({ items: [message], nextCursor: null })
    const wrapper = mountFeed()
    await flushPromises()

    const items = wrapper.findComponent(virtualScroll).props('items') as RecordedEvent[]
    expect(items[0]).toBe(message)
    expect(items[0]!.payload).toBe(payload)
    expect(isProxy(items[0])).toBe(false)
    expect(isProxy(items[0]!.payload)).toBe(false)
  })

  it('连续播放120帧且没有新消息时保留列表，不重复触发跟随', async () =>
  {
    eventsRequest.mockResolvedValue({ items: [event({ text: '正在阅读' }), event({ sequence: 2, eventId: 'future', offsetMs: 3000, text: '未来消息' })], nextCursor: null })
    const wrapper = mountFeed()
    await flushPromises()
    const scroller = wrapper.findComponent(virtualScroll)
    const items = scroller.props('items')
    scrollToIndex.mockClear()
    for (let frame = 1; frame <= 120; frame++)
    {
      await wrapper.setProps({ offsetMs: 2000 + frame * 5 })
    }
    expect(scroller.props('items')).toBe(items)
    expect(scrollToIndex).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('正在阅读')
    expect(wrapper.text()).not.toContain('未来消息')
  })

  it('消息到达时才显示，并且只跟随一次、对齐列表末端', async () =>
  {
    eventsRequest.mockResolvedValue({ items: [event({ text: '先前消息' }), event({ sequence: 2, eventId: 'next', offsetMs: 2500, text: '新消息' })], nextCursor: null })
    const wrapper = mountFeed()
    await flushPromises()
    scrollToIndex.mockClear()
    await wrapper.setProps({ offsetMs: 2499 })
    expect(wrapper.text()).not.toContain('新消息')
    expect(scrollToIndex).not.toHaveBeenCalled()
    await wrapper.setProps({ offsetMs: 2500 })
    await flushPromises()
    expect(wrapper.text()).toContain('新消息')
    expect(scrollToIndex).toHaveBeenCalledExactlyOnceWith(1, 'end')
    await wrapper.setProps({ offsetMs: 2516 })
    await wrapper.setProps({ offsetMs: 2532 })
    expect(scrollToIndex).toHaveBeenCalledTimes(1)
  })

  it('直播轮询返回相同归档记录时不重建列表或重新滚动', async () =>
  {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
    eventsRequest.mockImplementation(async () => ({ items: [event({ text: '保持可读' })], nextCursor: null }))
    const wrapper = mountFeed(2000, true)
    await flushPromises()
    const scroller = wrapper.findComponent(virtualScroll)
    const items = scroller.props('items')
    scrollToIndex.mockClear()
    await vi.advanceTimersByTimeAsync(10000)
    await flushPromises()
    expect(eventsRequest).toHaveBeenCalledTimes(2)
    expect(scroller.props('items')).toBe(items)
    expect(scrollToIndex).not.toHaveBeenCalled()
  })

  it('向上阅读时保留消息窗口，恢复跟随后再呈现新消息', async () =>
  {
    eventsRequest.mockResolvedValue({ items: [event({ text: '正在阅读' }), event({ sequence: 2, eventId: 'next', offsetMs: 3000, text: '后来消息' })], nextCursor: null })
    const wrapper = mountFeed()
    await flushPromises()
    const scroller = wrapper.findComponent(virtualScroll)
    const items = scroller.props('items')
    await wrapper.get('.event-scroll').trigger('wheel', { deltaY: -200 })
    scrollToIndex.mockClear()
    await wrapper.setProps({ offsetMs: 3500 })
    await flushPromises()
    expect(scroller.props('items')).toBe(items)
    expect(wrapper.text()).toContain('正在阅读')
    expect(wrapper.text()).not.toContain('后来消息')
    expect(scrollToIndex).not.toHaveBeenCalled()
    await wrapper.get('.feed-heading v-btn-stub').trigger('click')
    await flushPromises()
    expect(wrapper.text()).toContain('后来消息')
    expect(scrollToIndex).toHaveBeenCalledExactlyOnceWith(1, 'end')
  })

  it('暂停阅读时直播增量仍入缓存，恢复跟随后保留历史并显示新增消息', async () =>
  {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
    eventsRequest.mockResolvedValueOnce({ items: [event({ offsetMs: 120000, text: '保留的旧消息' })], nextCursor: null })
    const wrapper = mountFeed(130000, true)
    await flushPromises()
    const scroller = wrapper.findComponent(virtualScroll)
    const items = scroller.props('items')
    await wrapper.get('.event-scroll').trigger('wheel', { deltaY: -200 })
    scrollToIndex.mockClear()
    eventsRequest.mockResolvedValue({ items: [event({ offsetMs: 120000, text: '保留的旧消息' }), event({ sequence: 2, eventId: 'new-window', offsetMs: 299000, text: '新窗口消息' })], nextCursor: null })
    await wrapper.setProps({ offsetMs: 300000 })
    await vi.advanceTimersByTimeAsync(10000)
    await flushPromises()
    expect(eventsRequest).toHaveBeenCalledTimes(2)
    expect(scroller.props('items')).toBe(items)
    expect(wrapper.text()).toContain('保留的旧消息')
    expect(wrapper.text()).not.toContain('新窗口消息')
    expect(scrollToIndex).not.toHaveBeenCalled()
    await wrapper.get('.feed-heading v-btn-stub').trigger('click')
    await flushPromises()
    expect(wrapper.text()).toContain('保留的旧消息')
    expect(wrapper.text()).toContain('新窗口消息')
    expect(scrollToIndex).toHaveBeenCalledExactlyOnceWith(1, 'end')
  })

  it('程序滚动与行高调整不会自行关闭跟随', async () =>
  {
    eventsRequest.mockResolvedValue({ items: [event({ text: '正常消息' })], nextCursor: null })
    const wrapper = mountFeed()
    await flushPromises()
    const element = wrapper.get('.event-scroll').element
    Object.defineProperties(element, { scrollHeight: { value: 2000 }, clientHeight: { value: 500 }, scrollTop: { value: 100, writable: true } })
    await wrapper.get('.event-scroll').trigger('scroll')
    expect(wrapper.get('.feed-heading v-btn-stub').attributes('color')).toBe('primary')
  })

  it('经过隐藏协议消息的时间点时不重建可见列表', async () =>
  {
    eventsRequest.mockResolvedValue({ items: [event({ text: '保留消息' }), event({ sequence: 2, kind: 'Raw', offsetMs: 2500 }), event({ sequence: 3, kind: 'RoomState', offsetMs: 2600 })], nextCursor: null })
    const wrapper = mountFeed()
    await flushPromises()
    const scroller = wrapper.findComponent(virtualScroll)
    const items = scroller.props('items')
    scrollToIndex.mockClear()
    await wrapper.setProps({ offsetMs: 2700 })
    await flushPromises()
    expect(scroller.props('items')).toBe(items)
    expect(scrollToIndex).not.toHaveBeenCalled()
  })

  it('轮询只追加未来记录时不重建当前可见列表', async () =>
  {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
    eventsRequest.mockResolvedValueOnce({ items: [event({ text: '当前消息' })], nextCursor: null })
    const wrapper = mountFeed(2000, true)
    await flushPromises()
    const scroller = wrapper.findComponent(virtualScroll)
    const items = scroller.props('items')
    scrollToIndex.mockClear()
    eventsRequest.mockResolvedValue({ items: [event({ text: '当前消息' }), event({ sequence: 2, eventId: 'future', offsetMs: 25000, text: '未来记录' })], nextCursor: null })
    await vi.advanceTimersByTimeAsync(10000)
    await flushPromises()
    expect(scroller.props('items')).toBe(items)
    expect(wrapper.text()).not.toContain('未来记录')
    expect(scrollToIndex).not.toHaveBeenCalled()
  })

  it('直播窗口前移但未淘汰可见消息时保留列表', async () =>
  {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
    eventsRequest.mockImplementation(async () => ({ items: [event({ offsetMs: 135000, text: '仍在窗口内' })], nextCursor: null }))
    const wrapper = mountFeed(140000, true)
    await flushPromises()
    const scroller = wrapper.findComponent(virtualScroll)
    const items = scroller.props('items')
    scrollToIndex.mockClear()
    await wrapper.setProps({ offsetMs: 145000 })
    await vi.advanceTimersByTimeAsync(10000)
    await flushPromises()
    expect(eventsRequest).toHaveBeenCalledTimes(2)
    expect(scroller.props('items')).toBe(items)
    expect(scrollToIndex).not.toHaveBeenCalled()
  })

  it('明确暂停后程序滚到列表底部仍保持暂停，用户向下滚到底才恢复', async () =>
  {
    eventsRequest.mockResolvedValue({ items: [event({ text: '暂停保留' })], nextCursor: null })
    const wrapper = mountFeed()
    await flushPromises()
    await wrapper.get('.feed-heading v-btn-stub').trigger('click')
    const element = wrapper.get('.event-scroll').element
    Object.defineProperties(element, { scrollHeight: { value: 1000 }, clientHeight: { value: 500 }, scrollTop: { value: 500, writable: true } })
    await wrapper.get('.event-scroll').trigger('scroll')
    expect(wrapper.get('.feed-heading v-btn-stub').attributes('color')).toBe('secondary')
    await wrapper.get('.event-scroll').trigger('wheel', { deltaY: 100 })
    await wrapper.get('.event-scroll').trigger('scroll')
    expect(wrapper.get('.feed-heading v-btn-stub').attributes('color')).toBe('primary')
  })

  it('显式导航只在本地恢复跟随，普通位置回退不解除暂停', async () =>
  {
    eventsRequest.mockResolvedValueOnce({ items: [event({ text: '正在阅读' }), event({ sequence: 2, eventId: 'near-navigation', offsetMs: 2100, text: '同区间新消息' })], nextCursor: null })
    const wrapper = mountFeed()
    await flushPromises()
    await wrapper.get('.event-scroll').trigger('wheel', { deltaY: -200 })
    await wrapper.setProps({ offsetMs: 1990 })
    await flushPromises()
    expect(wrapper.get('.feed-heading v-btn-stub').attributes('color')).toBe('secondary')
    expect(eventsRequest).toHaveBeenCalledTimes(1)
    await wrapper.setProps({ offsetMs: 2200, navigationRevision: 1 })
    await flushPromises()
    expect(eventsRequest).toHaveBeenCalledTimes(1)
    expect(wrapper.get('.feed-heading v-btn-stub').attributes('color')).toBe('primary')
    expect(wrapper.text()).toContain('同区间新消息')
  })

  it('暂停阅读后显式向前导航会恢复跟随并定位目标之前的历史', async () =>
  {
    eventsRequest.mockResolvedValueOnce({ items: [event({ offsetMs: 120000, text: '暂停中的旧消息' }), event({ sequence: 2, eventId: 'navigation-target', offsetMs: 359000, text: '导航目标消息' })], nextCursor: null })
    const wrapper = mountFeed(130000, true)
    await flushPromises()
    await wrapper.get('.event-scroll').trigger('wheel', { deltaY: -200 })
    scrollToIndex.mockClear()
    await wrapper.setProps({ offsetMs: 360000, navigationRevision: 1 })
    await flushPromises()
    expect(eventsRequest).toHaveBeenCalledTimes(1)
    expect(wrapper.get('.feed-heading v-btn-stub').attributes('color')).toBe('primary')
    expect(wrapper.text()).toContain('暂停中的旧消息')
    expect(wrapper.text()).toContain('导航目标消息')
    expect(scrollToIndex).toHaveBeenLastCalledWith(1, 'end')
  })

  it('向后跳转会隐藏未来消息和红包开奖结果', async () =>
  {
    eventsRequest.mockResolvedValue({ items: pocketEvents(), nextCursor: null })
    const wrapper = mountFeed(3500)
    await flushPromises()
    expect(wrapper.text()).toContain('中奖用户')
    await wrapper.setProps({ offsetMs: 1500, navigationRevision: 1 })
    await flushPromises()
    expect(wrapper.text()).not.toContain('中奖用户')
    expect(wrapper.text()).toContain('待开奖')
  })
})
