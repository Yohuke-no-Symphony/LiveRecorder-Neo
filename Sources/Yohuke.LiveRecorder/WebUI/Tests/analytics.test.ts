// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { defineComponent, h } from 'vue'
import { createMemoryHistory, createRouter } from 'vue-router'
import AnalyticsView from '../src/views/AnalyticsView.vue'
import type { Analytics, BroadcastSession } from '../src/models/media'
import { formatMoney } from '../src/utilities/format'
import { i18n } from '../src/plugins/i18n'

const { sessions, streamers, analytics, sessionDetail } = vi.hoisted(() => ({ sessions: vi.fn(), streamers: vi.fn(), analytics: vi.fn(), sessionDetail: vi.fn() }))
vi.mock('../src/apis/MediaAPI', () => ({ MediaAPI: { sessions, streamers, analytics, session: sessionDetail } }))
const card = defineComponent({ setup: (_, { slots }) => () => h('div', slots.default?.()) })
const table = defineComponent({ setup: (_, { slots }) => () => h('table', slots.default?.()) })
let wrapper: VueWrapper | undefined
beforeEach(() => { i18n.global.locale.value = 'zh-CN' })
afterEach(() => { wrapper?.unmount(); vi.restoreAllMocks() })

describe('分析金额展示', () =>
{
  it('保留一瓜子的0.001元面值，同时维持普通金额两位小数', () =>
  {
    expect(formatMoney(0.001)).toContain('0.001')
    expect(formatMoney(1.001)).toContain('1.001')
    expect(formatMoney(30)).toContain('30.00')
    expect(formatMoney(0)).toContain('0.00')
  })

  it('实际分析页使用observedAmount，分列金币/协议支付/报价和未知价格，报价不重复加总', async () =>
  {
    vi.spyOn(window, 'scrollTo').mockImplementation(() => {})
    const source: BroadcastSession = { id: 's1', streamerId: 'streamer1', roomId: 1, title: '测试直播', coverUrl: '', createdAtUtc: '2026-11-12T00:00:00Z', startedAtUtc: '2026-11-12T00:00:00Z', endedAtUtc: '2026-11-12T00:01:00Z', lastMediaAtUtc: '2026-11-12T00:01:00Z', state: 'Completed', hasDanmu: true, totalBytes: 1024, isTrashed: false, mediaRevision: 1 }
    const report: Analytics = {
      observedAmount: 1.001, goldGiftValue: 0.001, protocolPaidAmount: 1, quotedAmount: 138, paidRevenue: 999,
      unknownAmountCount: 2, unknownGiftQuantity: 3, unidentifiedFinancialEvents: 4, giftQuantity: 5,
      activeUsers: 1, danmuCount: 2, giftCount: 3, superChatCount: 1, guardCount: 1, redPocketCount: 1, coverageGaps: 0,
      topUsers: [{ userId: 'u1', userName: '用户甲', messages: 2, paidAmount: 0.001 }], buckets: [],
    }
    sessions.mockResolvedValue([source])
    streamers.mockResolvedValue([])
    analytics.mockResolvedValue(report)
    sessionDetail.mockResolvedValue({ session: source, durationMs: 60000, segments: [], gaps: [] })
    const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/', component: { render: () => null } }] })
    await router.push('/')
    wrapper = mount(AnalyticsView, { global: { plugins: [router, i18n], stubs: { VCard: card, VTable: table, VSelect: true, VAlert: true, VProgressLinear: true, VMenu: true, VBtn: true, VBtnToggle: true, VDivider: true, VListItem: true, VProgressCircular: true, VIcon: true } } })
    await flushPromises()
    await vi.waitFor(() => expect(wrapper!.findAll('.amount-row')).toHaveLength(6))
    const summary = wrapper.get('.analytics-stats')
    expect(summary.get('.stat-label').text()).toBe('观测总额')
    expect(summary.get('.stat-value').text()).toContain('1.001')
    expect(summary.text()).not.toContain('139.001')
    expect(summary.text()).not.toContain('999')
    const amounts = Object.fromEntries(wrapper.findAll('.amount-row').map(row => [row.get('dt').text(), row.get('dd').text()]))
    expect(amounts['金币面值']).toContain('0.001')
    expect(amounts['协议支付']).toContain('1.00')
    expect(amounts['报价']).toContain('138.00')
    expect(amounts['未知价格记录']).toBe('2 条')
    expect(amounts['未定价礼物数量']).toBe('3 个')
    expect(amounts['缺少可靠标识的金额记录']).toBe('4 条')
    expect(wrapper.findAll('tbody td').at(-1)?.text()).toContain('0.001')
  })
})
