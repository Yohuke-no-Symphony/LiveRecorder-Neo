// @vitest-environment jsdom
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest'
import { config, flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { defineComponent, h } from 'vue'
import { i18n } from '../src/plugins/i18n'
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
vi.mock('../src/apis/MediaAPI', () => ({ MediaAPI: { events: eventsRequest } }))
const mounted: VueWrapper[] = []
const originalPlugins = [...config.global.plugins]
const card = defineComponent({ setup: (_, { slots }) => () => h('div', slots.default?.()) })
const virtualScroll = defineComponent({
  props: ['items'],
  setup(props, { slots, expose })
  {
    expose({ scrollToIndex: () => {} })
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
beforeEach(() => { i18n.global.locale.value = 'zh-CN'; hlsConstructor.mockClear(); supported.mockClear(); eventsRequest.mockReset() })
afterEach(() => { mounted.splice(0).forEach(wrapper => wrapper.unmount()); vi.restoreAllMocks(); vi.unstubAllGlobals() })

describe('实际消息内容组件', () =>
{
  it('弹幕使用纯文本、表情图片和真实回复/身份/粉丝牌，拒绝脚本图片地址', () =>
  {
    const wrapper = mount(DanmuContent, { props: { event: event({ payload: { content: '<img src=x onerror=alert(1)> [笑] [坏]', replyUserName: '<b>用户乙</b>', isRoomAdmin: true, roomGuardLevel: 2, medalName: '粉丝牌', medalLevel: 12, emoticons: [{ text: '[笑]', url: 'https://cdn.example.test/smile.png', width: 24, height: 24 }, { text: '[坏]', url: 'javascript:alert(1)' }] } }) } })
    mounted.push(wrapper)
    expect(wrapper.text()).toContain('<img src=x onerror=alert(1)>')
    expect(wrapper.text()).toContain('回复 <b>用户乙</b>：')
    expect(wrapper.text()).toContain('房管')
    expect(wrapper.text()).toContain('提督')
    expect(wrapper.get('.medal').text()).toContain('粉丝牌')
    expect(wrapper.get('.medal b').text()).toBe('12')
    expect(wrapper.findAll('img')).toHaveLength(1)
    expect(wrapper.get('img').attributes('src')).toBe('https://cdn.example.test/smile.png')
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

  it('大航海显示档位、数量，仅协议报价标注“报价”', async () =>
  {
    const value = event({ kind: 'Guard', quantity: 2, amount: 1380, priceSource: 'ProtocolQuote', payload: { guardLevel: 2 } })
    const wrapper = mount(GuardContent, { props: { event: value } })
    mounted.push(wrapper)
    expect(wrapper.text()).toContain('提督')
    expect(wrapper.text()).toContain('×2')
    expect(wrapper.text()).toContain('报价')
    await wrapper.setProps({ event: { ...value, priceSource: 'ProtocolPaid' } })
    expect(wrapper.text()).not.toContain('报价')
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
