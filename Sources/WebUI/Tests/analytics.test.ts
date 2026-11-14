// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { defineComponent, h, nextTick } from 'vue'
import { createPinia } from 'pinia'
import RelationshipCharts from '../src/components/analytics/RelationshipCharts.vue'
import { createMemoryHistory, createRouter } from 'vue-router'
import AnalyticsView from '../src/views/AnalyticsView.vue'
import ActivityChart from '../src/components/analytics/ActivityChart.vue'
import UserRanking from '../src/components/analytics/UserRanking.vue'
import type { Analytics, BroadcastSession } from '../src/models/media'
import { formatMoney } from '../src/utilities/format'
import { i18n } from '../src/plugins/i18n'

const { sessions, streamers, analytics, sessionDetail } = vi.hoisted(() => ({ sessions: vi.fn(), streamers: vi.fn(), analytics: vi.fn(), sessionDetail: vi.fn() }))
vi.mock('../src/apis/MediaAPI', () => ({ MediaAPI: { sessions, streamers, analytics, session: sessionDetail } }))
const { aiStatus } = vi.hoisted(() => ({ aiStatus: vi.fn() }))
vi.mock('../src/apis/AiAnalysisAPI', () => ({ AiAnalysisAPI: { status: aiStatus } }))
const card = defineComponent({ setup: (_, { slots }) => () => h('div', slots.default?.()) })
const table = defineComponent({ setup: (_, { slots }) => () => h('table', slots.default?.()) })
const select = defineComponent({ props: ['modelValue', 'items', 'label'], emits: ['update:modelValue'], setup: (props, { emit }) => () => h('select', { value: props.modelValue, 'aria-label': props.label, onChange: (event: Event) => emit('update:modelValue', (event.target as HTMLSelectElement).value) }, props.items.map((item: { value: string; title: string }) => h('option', { value: item.value }, item.title))) })
const button = defineComponent({ emits: ['click'], setup: (_, { slots, emit }) => () => h('button', { onClick: () => emit('click') }, slots.default?.()) })
const stubs = { RelationshipCharts: true, AiAnalysisPanel: true, AiAnalysisToolbar: true, AiAnalysisDetailsDialog: true, VCard: card, VTable: table, VSelect: select, VAlert: card, VProgressLinear: true, VMenu: true, VTooltip: true, VBtn: button, VBtnToggle: card, VDivider: true, VListItem: true, VProgressCircular: true, VIcon: true }
const mounted: VueWrapper[] = []

function source(id = 's1'): BroadcastSession
{
  return { id, streamerId: 'streamer1', roomId: 1, title: `测试直播 ${id}`, coverUrl: '', createdAtUtc: '2026-11-12T00:00:00Z', startedAtUtc: '2026-11-12T00:00:00Z', endedAtUtc: '2026-11-12T00:01:00Z', lastMediaAtUtc: '2026-11-12T00:01:00Z', state: 'Completed', hasDanmu: true, totalBytes: 1024, isTrashed: false, isPermanent: false, mediaRevision: 1 }
}
function user(id: string, name: string)
{
  return { userId: id, userName: name, messages: 2, paidAmount: 0.001, danmuCount: 2, superChatAmount: 0, superChatCount: 0, giftAmount: 0.001, giftQuantity: 5, guardAmount: 0, guardQuantity: 0, redPocketAmount: null, redPocketCount: 0 }
}
function activityBucket(offsetMs: number, amount = 0): Analytics['buckets'][number]
{
  return { offsetMs, messages: 1, paidAmount: amount, danmuCount: 1, activeUsers: 1, danmuUsers: 1, payingUsers: amount > 0 ? 1 : 0, superChatAmount: amount, superChatCount: amount > 0 ? 1 : 0, onlineAverage: null, onlinePeak: null, onlineObservedMs: 0, durationMs: 60000, giftAmount: 0, guardAmount: 0 }
}
function report(): Analytics
{
  const first = user('u1', '用户甲')
  return {
    fromMs: 0, toMs: 60000, online: { peak: null, average: null, observedMs: 0, rangeMs: 60000, coverageRate: 0, intervals: [] },
    observedAmount: 1.001, goldGiftValue: 0.001, protocolPaidAmount: 1, quotedAmount: 138, paidRevenue: 999,
    unknownAmountCount: 2, unknownGiftQuantity: 3, unidentifiedFinancialEvents: 4, giftQuantity: 5,
    activeUsers: 2, payingUsers: 1, danmuUsers: 1, danmuCount: 2, giftCount: 3, superChatCount: 1, guardCount: 1, redPocketCount: 1, coverageGaps: 0,
    categories: [{ kind: 'SuperChat', amount: 1, count: 1, quantity: 1, users: 1 }, { kind: 'Gift', amount: 0.001, count: 3, quantity: 5, users: 1 }, { kind: 'Guard', amount: 0, count: 1, quantity: 1, users: 1 }],
    topUsers: [first], userRankings: { danmuCount: [first], superChatAmount: [], superChatCount: [], giftAmount: [first], giftQuantity: [first], guardAmount: [], guardQuantity: [], redPocketAmount: [], redPocketCount: [] }, buckets: [],
    translations: { count: 0, users: 0, topUsers: [], buckets: [] },
  }
}
function deferred<T>()
{
  let resolve!: (value: T) => void
  let reject!: (reason: Error) => void
  const promise = new Promise<T>((accept, fail) => { resolve = accept; reject = fail })
  return { promise, resolve, reject }
}
async function mountView(path = '/analytics')
{
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/analytics', component: { render: () => null } }, { path: '/sessions/:id', component: { render: () => null } }] })
  await router.push(path)
  const wrapper = mount(AnalyticsView, { global: { plugins: [createPinia(), router, i18n], stubs } })
  mounted.push(wrapper)
  await flushPromises()
  return { wrapper, router }
}
beforeEach(() =>
{
  vi.resetAllMocks()
  aiStatus.mockResolvedValue({ enabled: false, canGenerate: false, currentJob: null, latestCompleted: null, latestPartial: null, stale: false })
  i18n.global.locale.value = 'zh-CN'
  sessions.mockResolvedValue([source()])
  streamers.mockResolvedValue([])
  analytics.mockResolvedValue(report())
  sessionDetail.mockImplementation(async (id: string) => ({ session: source(id), durationMs: 60000, segments: [], gaps: [] }))
})
afterEach(() => { mounted.splice(0).forEach(wrapper => wrapper.unmount()); vi.useRealTimers(); vi.restoreAllMocks(); i18n.global.locale.value = 'zh-CN' })

describe('分析流水与分类', () =>
{
  it('在线人数区分零与未知，并显示观测覆盖率', async () =>
  {
    const data = report()
    data.online = { peak: 0, average: 0, observedMs: 30000, rangeMs: 60000, coverageRate: .5, intervals: [] }
    analytics.mockResolvedValue(data)
    const { wrapper } = await mountView()
    expect(wrapper.get('[data-stat="peakOnline"]').text()).toContain('0')
    expect(wrapper.get('[data-stat="averageOnline"]').text()).not.toContain('无数据')
    expect(wrapper.text()).toContain('50%')
  })

  it('选区请求使用后端统计，恢复全场后忽略迟到的选区响应', async () =>
  {
    vi.useFakeTimers()
    const { wrapper } = await mountView()
    const pending = deferred<Analytics>()
    analytics.mockReturnValueOnce(pending.promise)
    wrapper.findComponent(RelationshipCharts).vm.$emit('range', { fromMs: 10000, toMs: 50000 })
    await vi.advanceTimersByTimeAsync(250)
    expect(analytics).toHaveBeenLastCalledWith('s1', { fromMs: 10000, toMs: 50000 }, expect.any(AbortSignal))
    wrapper.findComponent(RelationshipCharts).vm.$emit('range', undefined)
    pending.resolve({ ...report(), danmuCount: 999 })
    await flushPromises()
    expect(wrapper.get('[data-stat="danmuCount"] .stat-value').text()).toBe('2')
  })

  it('选区请求失败时回到统计数字对应的上一个有效范围', async () =>
  {
    vi.useFakeTimers()
    const { wrapper } = await mountView()
    const firstRange = { fromMs: 10000, toMs: 30000 }
    analytics.mockResolvedValueOnce({ ...report(), ...firstRange, danmuCount: 7 })
    wrapper.findComponent(RelationshipCharts).vm.$emit('range', firstRange)
    await vi.advanceTimersByTimeAsync(250)
    await flushPromises()
    analytics.mockRejectedValueOnce(new Error('选区读取失败'))
    wrapper.findComponent(RelationshipCharts).vm.$emit('range', { fromMs: 30000, toMs: 50000 })
    await vi.advanceTimersByTimeAsync(250)
    await flushPromises()
    expect(wrapper.text()).toContain('选区读取失败')
    expect(wrapper.get('[data-stat="danmuCount"] .stat-value').text()).toBe('7')
    expect(wrapper.findComponent(RelationshipCharts).props('range')).toEqual(firstRange)
  })

  it('保留一瓜子的0.001元面值，同时维持普通金额两位小数', () =>
  {
    expect(formatMoney(0.001)).toContain('0.001')
    expect(formatMoney(1.001)).toContain('1.001')
    expect(formatMoney(30)).toContain('30.00')
    expect(formatMoney(0)).toContain('0.00')
  })

  it('按SC礼物大航海展示独立卡片，总流水不加入报价和旧兼容字段', async () =>
  {
    const { wrapper } = await mountView()
    const summary = wrapper.get('.analytics-stats')
    expect(summary.get('.stat-label').text()).toBe('总流水')
    expect(summary.get('.stat-value').text()).toContain('1.001')
    expect(summary.text()).not.toContain('139.001')
    expect(summary.text()).not.toContain('999')
    expect(wrapper.findAll('.category-revenue-card')).toHaveLength(3)
    expect(wrapper.get('.category-revenue-card[data-kind="Gift"]').text()).toContain('0.001')
    expect(wrapper.get('.category-revenue-card[data-kind="Guard"]').text()).toContain('大航海')
    expect(wrapper.get('[data-stat="payingUsers"]').text()).toContain('1')
    expect(wrapper.get('[data-stat="payingParticipation"]').text()).toContain('50%')
    expect(wrapper.get('[data-stat="payingUsers"] .stat-label').text()).toBe('付费人数')
    expect(wrapper.get('[data-stat="payingUsers"] v-tooltip-stub').attributes('text')).toContain('同一用户只计一次')
    expect(wrapper.get('[data-stat="payingParticipation"] v-tooltip-stub').attributes('text')).toContain('付费人数 ÷ 互动用户数')
    expect(wrapper.get('[data-stat="payingParticipation"]').find('p').exists()).toBe(false)
    expect(wrapper.find('v-menu-stub').exists()).toBe(false)
    expect(wrapper.find('.amount-diagnostics').exists()).toBe(false)
    i18n.global.locale.value = 'en-US'
    await nextTick()
    expect(wrapper.get('.analytics-stats').text()).toContain('Total revenue')
    expect(wrapper.get('.category-revenue-card[data-kind="Guard"]').text()).toContain('Memberships')
  })

  it('使用所选指标的独立全量排行榜，红包未知金额不显示为零', async () =>
  {
    const data = report()
    data.userRankings.superChatAmount = [{ ...user('rich', '付费榜用户'), messages: 1, danmuCount: 0, superChatAmount: 100, paidAmount: 100 }]
    data.userRankings.redPocketAmount = [{ ...user('red', '红包用户'), redPocketCount: 1 }]
    const wrapper = mount(UserRanking, { props: { rankings: data.userRankings }, global: { plugins: [i18n], stubs } })
    mounted.push(wrapper)
    expect(wrapper.text()).toContain('用户甲')
    await wrapper.get('select').setValue('superChatAmount')
    expect(wrapper.text()).toContain('付费榜用户')
    expect(wrapper.text()).not.toContain('用户甲')
    await wrapper.get('select').setValue('redPocketAmount')
    expect(wrapper.get('.ranking-value').text()).toBe('未知')
    expect(wrapper.text()).toContain('不计入总流水')
  })

  it('排行榜头像和姓名链接到同一B站主页，保留完整UID并在图片失败后保留链接', async () =>
  {
    const data = report()
    const viewer = user('9223372036854775807', '用户甲')
    data.userRankings.danmuCount = [viewer]
    const wrapper = mount(UserRanking, { props: { rankings: data.userRankings }, global: { plugins: [i18n], stubs } })
    mounted.push(wrapper)
    const link = wrapper.get('tbody a')
    expect(link.attributes('href')).toBe('https://space.bilibili.com/9223372036854775807')
    expect(link.attributes('target')).toBe('_blank')
    expect(link.attributes('rel')?.split(' ')).toEqual(expect.arrayContaining(['noopener', 'noreferrer']))
    expect(link.get('.analytics-user-name').text()).toBe(viewer.userName)
    expect(link.attributes('title')).toBe(viewer.userName)
    const avatar = link.get('img')
    expect(new URL(avatar.attributes('src')!).pathname).toBe('/api/audience/9223372036854775807/avatar')
    expect(avatar.attributes('loading')).toBe('lazy')
    await avatar.trigger('error')
    expect(link.find('img').exists()).toBe(false)
    expect(link.get('.avatar-initial').text()).toBe('用')
    expect(link.get('.analytics-user-name').text()).toBe(viewer.userName)
    expect(link.attributes('href')).toBe('https://space.bilibili.com/9223372036854775807')
  })

  it.each(['', '0', '-1', '00123', 'unknown', '9223372036854775808', '123/avatar', 'javascript:alert(1)'])('无有效UID %s时保留姓名与占位头像，不生成链接或图片请求', id =>
  {
    const data = report()
    data.userRankings.danmuCount = [user(id, '访客甲')]
    const wrapper = mount(UserRanking, { props: { rankings: data.userRankings }, global: { plugins: [i18n], stubs } })
    mounted.push(wrapper)
    expect(wrapper.get('tbody').text()).toContain('访客甲')
    expect(wrapper.get('tbody .avatar-initial').text()).toBe('访')
    expect(wrapper.find('tbody a').exists()).toBe(false)
    expect(wrapper.find('tbody img').exists()).toBe(false)
  })

  it('切换排行榜指标后同步头像和用户主页', async () =>
  {
    const data = report()
    data.userRankings.danmuCount = [user('123456', '用户甲')]
    data.userRankings.giftAmount = [user('654321', '用户乙')]
    const wrapper = mount(UserRanking, { props: { rankings: data.userRankings }, global: { plugins: [i18n], stubs } })
    mounted.push(wrapper)
    await wrapper.get('tbody img').trigger('error')
    await wrapper.get('select').setValue('giftAmount')
    expect(wrapper.get('tbody a').attributes('href')).toBe('https://space.bilibili.com/654321')
    expect(new URL(wrapper.get('tbody img').attributes('src')!).pathname).toBe('/api/audience/654321/avatar')
    expect(wrapper.get('.analytics-user-name').text()).toBe('用户乙')
  })
})

describe('指定录播分析导航', () =>
{
  it('直接分析不在第一页中的指定录播，并补齐选择项', async () =>
  {
    const { wrapper } = await mountView('/analytics?sessionId=older-session')
    expect(analytics).toHaveBeenCalledTimes(1)
    expect(analytics).toHaveBeenCalledWith('older-session')
    expect(sessionDetail).toHaveBeenCalledWith('older-session')
    expect(wrapper.get('select').element.value).toBe('older-session')
    expect(wrapper.findAll('option').some(option => option.text().includes('测试直播 older-session'))).toBe(true)
  })

  it('切换与浏览器返回时同步query，过期请求的失败不会覆盖新录播', async () =>
  {
    const old = deferred<Analytics>()
    analytics.mockImplementation((id: string) => id === 'old' ? old.promise : Promise.resolve({ ...report(), observedAmount: 222 }))
    const { wrapper, router } = await mountView('/analytics?sessionId=old')
    await router.push('/analytics?sessionId=new')
    await flushPromises()
    expect(wrapper.get('.analytics-stats .stat-value').text()).toContain('222.00')
    old.reject(new Error('旧录播失败'))
    await flushPromises()
    expect(wrapper.text()).not.toContain('旧录播失败')
    analytics.mockResolvedValue({ ...report(), observedAmount: 333 })
    router.back()
    await vi.waitFor(() => expect(router.currentRoute.value.query.sessionId).toBe('old'))
    await flushPromises()
    expect(wrapper.get('.analytics-stats .stat-value').text()).toContain('333.00')
    await wrapper.get('select').setValue('s1')
    await flushPromises()
    expect(router.currentRoute.value.query.sessionId).toBe('s1')
  })

  it('媒体结束后的归档仍可查看数据，点击图表不离开分析页', async () =>
  {
    analytics.mockResolvedValue({ ...report(), buckets: [activityBucket(120000, 7)] })
    sessionDetail.mockImplementation(async (id: string) => ({ session: source(id), durationMs: 30000, segments: [], gaps: [] }))
    const { wrapper, router } = await mountView()
    await wrapper.get('[data-metric="cumulative"]').trigger('click')
    await wrapper.get('.chart-bucket').trigger('click')
    await flushPromises()
    expect(router.currentRoute.value.path).toBe('/analytics')
    expect(router.currentRoute.value.query.at).toBeUndefined()
    expect(wrapper.get('[role="tooltip"]').text()).toContain('02:00')
    expect(wrapper.get('[role="tooltip"]').text()).toContain('7.00')
  })

  it('没有可播放时长时仍可选择数据点查看详情', async () =>
  {
    analytics.mockResolvedValue({ ...report(), buckets: [activityBucket(60000, 7)] })
    sessionDetail.mockImplementation(async (id: string) => ({ session: source(id), durationMs: 0, segments: [], gaps: [] }))
    const { wrapper, router } = await mountView()
    await wrapper.get('.chart-bucket').trigger('click')
    await flushPromises()
    expect(router.currentRoute.value.path).toBe('/analytics')
    expect(router.currentRoute.value.query.at).toBeUndefined()
    expect(wrapper.get('[role="tooltip"]').text()).toContain('01:00')
    expect(wrapper.get('[role="tooltip"]').text()).toContain('弹幕')
  })
})

describe('独立同传统计', () =>
{
  it('同传板块展示完整总量与人数、个人条数和主页，不混入普通弹幕统计', async () =>
  {
    analytics.mockResolvedValue({ ...report(), translations: {
      count: 130, users: 101, topUsers: [{ userId: '123456', userName: '同传者甲', count: 12 }, ...Array.from({ length: 99 }, (_, index) => ({ userId: String(200000 + index), userName: `同传者${index + 2}`, count: 1 }))],
      buckets: [{ offsetMs: 0, count: 80 }, { offsetMs: 60000, count: 50 }],
    } })
    const { wrapper } = await mountView()
    expect(wrapper.get('[data-stat="danmuCount"] .stat-value').text()).toBe('2')
    expect(wrapper.get('[data-stat="activeUsers"] .stat-value').text()).toBe('2')
    const section = wrapper.get('.translation-analytics')
    expect(section.get('[data-stat="translationCount"] .stat-value').text()).toBe('130')
    expect(section.get('[data-stat="translationUsers"] .stat-value').text()).toBe('101')
    expect(section.get('.translation-ranking').text()).toContain('前 100 名')
    expect(section.findAll('tbody tr')).toHaveLength(100)
    expect(section.get('tbody a').attributes('href')).toBe('https://space.bilibili.com/123456')
    expect(new URL(section.get('tbody img').attributes('src')!).pathname).toBe('/api/audience/123456/avatar')
    expect(section.get('.translation-user-count').text()).toBe('12')
    expect(section.findAll('th').map(header => header.text())).toEqual(['排名', '用户', '同传条数'])
    expect(section.get('svg').attributes('aria-label')).toBe('同传量')
    expect(section.find('[data-metric]').exists()).toBe(false)
    await section.findAll('.chart-bucket')[1]!.trigger('focus')
    expect(section.get('[role="tooltip"]').text()).toContain('01:00')
    expect(section.get('.chart-tooltip-value').text()).toBe('50')
    expect(wrapper.get('.user-ranking').text()).not.toContain('同传者甲')
  })

  it('仅有同传时仍可查看统计，普通榜和同传榜使用各自的空态', async () =>
  {
    const data = report()
    data.activeUsers = 0
    data.danmuCount = 0
    data.userRankings.danmuCount = []
    data.translations = { count: 3, users: 1, topUsers: [{ userId: '123456', userName: '同传者甲', count: 3 }], buckets: [{ offsetMs: 0, count: 3 }] }
    analytics.mockResolvedValue(data)
    const { wrapper } = await mountView()
    expect(wrapper.get('.user-ranking').text()).toContain('暂无活跃用户数据')
    expect(wrapper.find('.translation-analytics svg').exists()).toBe(true)
    expect(wrapper.get('.translation-ranking').text()).toContain('同传者甲')
    analytics.mockResolvedValue(report())
    const { wrapper: empty } = await mountView()
    expect(empty.get('.translation-analytics').text()).toContain('暂无同传弹幕')
    expect(empty.get('.translation-ranking').text()).toContain('暂无同传者数据')
    expect(empty.find('.translation-analytics svg').exists()).toBe(false)
  })

  it('同传量图表复用键盘与悬停选择，零时长仍完整展示最后一分钟且不显示金额', async () =>
  {
    const wrapper = mount(ActivityChart, { props: { mode: 'translations', buckets: [{ offsetMs: 60000, count: 9 }], durationMs: 0 }, global: { plugins: [i18n], stubs } })
    mounted.push(wrapper)
    expect(wrapper.get('svg').attributes('aria-label')).toBe('同传量')
    expect(wrapper.text()).toContain('02:00')
    expect(wrapper.get('.chart-max').text()).toBe('9')
    const bucket = wrapper.get('.chart-bucket')
    await bucket.trigger('mouseenter')
    expect(wrapper.get('.chart-tooltip-value').text()).toBe('9')
    await bucket.trigger('mouseleave')
    expect(wrapper.find('[role="tooltip"]').exists()).toBe(false)
    await bucket.trigger('keydown', { key: 'Enter' })
    expect(bucket.attributes('aria-pressed')).toBe('true')
    expect(wrapper.get('[role="tooltip"]').text()).toContain('同传条数')
    await bucket.trigger('keydown', { key: 'Escape' })
    expect(wrapper.find('[role="tooltip"]').exists()).toBe(false)
    await bucket.trigger('focus')
    await wrapper.setProps({ buckets: [{ offsetMs: 60000, count: 11 }] })
    expect(wrapper.find('[role="tooltip"]').exists()).toBe(false)
    await wrapper.get('.chart-bucket').trigger('focus')
    expect(wrapper.get('.chart-tooltip-value').text()).toBe('11')
    expect(wrapper.text()).not.toContain('¥')
    expect(wrapper.emitted('seek')).toBeUndefined()
  })
})

describe('分时图表', () =>
{
  it.each([0, 60000])('零时长仍展示偏移%d的单个有效统计桶，坐标有限且保留完整分钟范围', async offsetMs =>
  {
    const buckets = [activityBucket(offsetMs, 7), activityBucket(-60000, 99), activityBucket(Number.POSITIVE_INFINITY, 99)]
    const wrapper = mount(ActivityChart, { props: { buckets, durationMs: 0 }, global: { plugins: [i18n], stubs } })
    mounted.push(wrapper)
    expect(wrapper.findAll('.chart-bucket')).toHaveLength(1)
    const bar = wrapper.get('.chart-bar')
    expect(Number(bar.attributes('width'))).toBeGreaterThan(1)
    for (const attribute of ['x', 'y', 'width', 'height'])
    {
      expect(Number.isFinite(Number(bar.attributes(attribute)))).toBe(true)
    }
    await wrapper.get('[data-metric="cumulative"]').trigger('click')
    expect(wrapper.get('.chart-max').text()).toContain('7.00')
    expect(wrapper.get('.chart-line').attributes('points')).not.toMatch(/NaN|Infinity/)
    expect(wrapper.text()).toContain(offsetMs === 0 ? '01:00' : '02:00')
  })

  it('媒体结束后的统计桶仍计入分类与累计流水，时间轴覆盖归档最后一分钟', async () =>
  {
    const wrapper = mount(ActivityChart, { props: { buckets: [activityBucket(0, 10), activityBucket(120000, 5)], durationMs: 60000 }, global: { plugins: [i18n], stubs } })
    mounted.push(wrapper)
    expect(wrapper.findAll('.chart-bucket')).toHaveLength(2)
    const lastBar = wrapper.findAll('.chart-bar').at(-1)!
    expect(Number(lastBar.attributes('x')) + Number(lastBar.attributes('width'))).toBeLessThanOrEqual(1000)
    expect(wrapper.text()).toContain('03:00')
    await wrapper.get('[data-metric="cumulative"]').trigger('click')
    expect(wrapper.get('.chart-max').text()).toContain('15.00')
    expect(wrapper.findAll('.chart-point')).toHaveLength(2)
  })

  it('空统计和零时长展示空状态，不生成无效图形坐标', () =>
  {
    const wrapper = mount(ActivityChart, { props: { buckets: [], durationMs: 0 }, global: { plugins: [i18n], stubs } })
    mounted.push(wrapper)
    expect(wrapper.find('.empty-state').exists()).toBe(true)
    expect(wrapper.find('svg').exists()).toBe(false)
  })

  it('切换分类流水和累计流水时，键盘选中的数据点展示对应指标', async () =>
  {
    const buckets = [
      { ...activityBucket(0, 10), messages: 4, danmuCount: 2, activeUsers: 3, danmuUsers: 2 },
      { ...activityBucket(60000, 5), messages: 2, superChatAmount: 0, giftAmount: 5 },
    ]
    const wrapper = mount(ActivityChart, { props: { buckets, durationMs: 120000 }, global: { plugins: [i18n], stubs } })
    mounted.push(wrapper)
    await wrapper.get('[data-metric="revenue"]').trigger('click')
    expect(wrapper.get('svg').attributes('aria-label')).toContain('分类流水')
    expect(wrapper.findAll('.chart-bar[data-series="superChatAmount"]')).toHaveLength(2)
    await wrapper.findAll('.chart-bucket')[1]!.trigger('keydown', { key: 'Enter' })
    expect(wrapper.emitted('seek')).toBeUndefined()
    const tooltip = wrapper.get('[role="tooltip"]')
    expect(tooltip.text()).toContain('01:00')
    expect(tooltip.text()).toContain('SC')
    expect(tooltip.text()).toContain('礼物')
    expect(tooltip.text()).toContain('5.00')
    expect(tooltip.text()).toContain('大航海')
    expect(wrapper.findAll('.chart-tooltip-value').map(value => value.text())).toEqual([formatMoney(0), formatMoney(5), formatMoney(0)])
    await wrapper.get('[data-metric="cumulative"]').trigger('click')
    expect(wrapper.get('.chart-max').text()).toContain('15.00')
    expect(wrapper.find('.chart-line').exists()).toBe(true)
    expect(wrapper.get('[role="tooltip"]').text()).toContain('累计流水')
    expect(wrapper.get('[role="tooltip"]').text()).toContain('15.00')
  })

  it('悬停显示分钟数据，离开关闭；点击选择后保留提示并可按Escape关闭', async () =>
  {
    const wrapper = mount(ActivityChart, { props: { buckets: [{ ...activityBucket(0), danmuCount: 12 }, activityBucket(60000)], durationMs: 120000 }, global: { plugins: [i18n], stubs } })
    mounted.push(wrapper)
    const point = wrapper.findAll('.chart-bucket')[0]!
    await point.trigger('mouseenter')
    expect(wrapper.get('[role="tooltip"]').text()).toContain('00:00')
    expect(wrapper.get('.chart-tooltip-value').text()).toBe('12')
    expect(point.attributes('aria-describedby')).toBe(wrapper.get('[role="tooltip"]').attributes('id'))
    await point.trigger('mouseleave')
    expect(wrapper.find('[role="tooltip"]').exists()).toBe(false)
    await point.trigger('click')
    await point.trigger('mouseleave')
    expect(wrapper.get('.chart-tooltip-value').text()).toBe('12')
    expect(point.attributes('aria-pressed')).toBe('true')
    await point.trigger('keydown', { key: 'Escape' })
    expect(wrapper.find('[role="tooltip"]').exists()).toBe(false)
    expect(wrapper.emitted('seek')).toBeUndefined()
  })

  it('聚焦展示互动与发言人数，数据快照更新后关闭旧提示', async () =>
  {
    const bucket = { ...activityBucket(0), activeUsers: 7, danmuUsers: 4 }
    const wrapper = mount(ActivityChart, { props: { buckets: [bucket], durationMs: 60000 }, global: { plugins: [i18n], stubs } })
    mounted.push(wrapper)
    await wrapper.get('[data-metric="users"]').trigger('click')
    await wrapper.get('.chart-bucket').trigger('focus')
    expect(wrapper.findAll('.chart-tooltip-value').map(value => value.text())).toEqual(['7', '4'])
    expect(wrapper.get('[role="tooltip"]').text()).toContain('互动用户')
    expect(wrapper.get('[role="tooltip"]').text()).toContain('发言人数')
    await wrapper.get('.chart-bucket').trigger('keydown', { key: ' ' })
    expect(wrapper.get('.chart-bucket').attributes('aria-pressed')).toBe('true')
    await wrapper.setProps({ buckets: [{ ...bucket, activeUsers: 9 }] })
    expect(wrapper.find('[role="tooltip"]').exists()).toBe(false)
    await wrapper.get('.chart-bucket').trigger('focus')
    expect(wrapper.findAll('.chart-tooltip-value').map(value => value.text())).toEqual(['9', '4'])
  })
})
