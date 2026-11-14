// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { defineComponent, h, ref } from 'vue'
import { createPinia } from 'pinia'
import { useAuthStore } from '../src/stores/auth'
import { createMemoryHistory, createRouter } from 'vue-router'
import AnalyticsView from '../src/views/AnalyticsView.vue'
import RelationshipCharts from '../src/components/analytics/RelationshipCharts.vue'
import AiAnalysisToolbar from '../src/components/analytics/AiAnalysisToolbar.vue'
import type { AiAnalysisJob, AiAnalysisStatus } from '../src/models/aiAnalysis'
import type { Analytics, BroadcastSession } from '../src/models/media'
import { i18n } from '../src/plugins/i18n'

const { status, generate, cancel, liveMarkerStatus, generateLiveMarkers, sessions, streamers, analytics, sessionDetail } = vi.hoisted(() => ({ status: vi.fn(), generate: vi.fn(), cancel: vi.fn(), liveMarkerStatus: vi.fn(), generateLiveMarkers: vi.fn(), sessions: vi.fn(), streamers: vi.fn(), analytics: vi.fn(), sessionDetail: vi.fn() }))
vi.mock('../src/apis/AiAnalysisAPI', () => ({ AiAnalysisAPI: { status, generate, cancel, liveMarkerStatus, generateLiveMarkers } }))
vi.mock('../src/apis/MediaAPI', () => ({ MediaAPI: { sessions, streamers, analytics, session: sessionDetail } }))
const card = defineComponent({ props: ['title'], setup: (props, { slots }) => () => h('section', [props.title ? h('h2', props.title) : null, slots.default?.(), slots.actions?.()]) })
const button = defineComponent({ props: { disabled: Boolean }, emits: ['click'], setup: (props, { slots, emit }) => () => h('button', { disabled: props.disabled, onClick: () => emit('click') }, slots.default?.()) })
const dialog = defineComponent({ props: { modelValue: Boolean }, setup: (props, { slots }) => () => props.modelValue ? h('div', { role: 'dialog' }, slots.default?.()) : null })
const progress = defineComponent({ setup: () => () => h('div', { role: 'progressbar' }) })
const menu = defineComponent({ setup: (_, { slots }) =>
{
  const opened = ref(false)
  return () => h('div', [
    slots.activator?.({ props: { 'aria-haspopup': 'menu', 'aria-expanded': opened.value, onClick: () => { opened.value = !opened.value } } }),
    opened.value ? h('div', { role: 'menu', onClick: () => { opened.value = false } }, slots.default?.()) : null,
  ])
} })
const menuItem = defineComponent({ props: { title: String, disabled: Boolean }, emits: ['click'], setup: (props, { emit }) => () => h('button', { role: 'menuitem', disabled: props.disabled, onClick: () => emit('click') }, props.title) })
const stubs = { ContentCard: card, CardHeader: card, EChartSurface: true, VBtn: button, VAlert: card, VProgressLinear: progress, VProgressCircular: true, VDialog: dialog, VMenu: menu, VList: card, VListItem: menuItem, VCard: card, VSelect: true, VTooltip: true, VDivider: true }
const mounted: VueWrapper[] = []

function source(id = 's1'): BroadcastSession
{
  return { id, streamerId: 'streamer1', roomId: 1, title: `测试直播 ${id}`, coverUrl: '', createdAtUtc: '2026-11-14T00:00:00Z', startedAtUtc: '2026-11-14T00:00:00Z', endedAtUtc: '2026-11-14T00:01:00Z', lastMediaAtUtc: '2026-11-14T00:01:00Z', state: 'Completed', hasDanmu: true, totalBytes: 1024, isTrashed: false, isPermanent: false, mediaRevision: 1 }
}
function report(): Analytics
{
  return {
    fromMs: 0, toMs: 60000, online: { peak: null, average: null, observedMs: 0, rangeMs: 60000, coverageRate: 0, intervals: [] },
    observedAmount: 0, goldGiftValue: 0, protocolPaidAmount: 0, quotedAmount: 0, paidRevenue: 0, unknownAmountCount: 0, unknownGiftQuantity: 0, unidentifiedFinancialEvents: 0, giftQuantity: 0,
    activeUsers: 0, payingUsers: 0, danmuUsers: 0, danmuCount: 0, giftCount: 0, superChatCount: 0, guardCount: 0, redPocketCount: 0, coverageGaps: 0, categories: [], topUsers: [], buckets: [],
    userRankings: { danmuCount: [], superChatAmount: [], superChatCount: [], giftAmount: [], giftQuantity: [], guardAmount: [], guardQuantity: [], redPocketAmount: [], redPocketCount: [] },
    translations: { count: 0, users: 0, topUsers: [], buckets: [] },
  }
}
function job(state: AiAnalysisJob['state'] = 'Running'): AiAnalysisJob
{
  return { id: 'new', sessionId: 's1', title: '录像标题', cancelRequested: false, state, progress: 25, phase: 'Analyzing', totalSegments: 4, completedSegments: 1, currentSegment: 2, currentSegmentStartMs: 15000, currentSegmentEndMs: 30000, contextCompactions: 3, promptTokens: 10, completionTokens: 5, estimatedTokens: 20, tokenBudget: 10000, modelRequests: 1, toolCalls: 2, errorCode: null, createdAtUtc: '', finishedAtUtc: null }
}
function result(): AiAnalysisStatus
{
  return { enabled: true, canGenerate: false, currentJob: null, latestPartial: null, stale: false, latestCompleted: {
    jobId: 'job', sessionId: 's1', sourceFingerprint: 'source', model: 'test', createdAtUtc: '2026-11-14T00:00:00Z', completedAtUtc: '2026-11-14T00:01:00Z', isComplete: true, overview: '共享复盘',
    topics: [
      { id: 'topic1', title: '演奏新曲', summary: '观众在讨论演奏', startMs: 10000, endMs: 30000, observations: '弹幕集中', evidence: [{ id: 'e1', offsetMs: 12000, text: '<script>ignored</script>', kind: 'Translation' }] },
      { id: 'topic2', title: '分享近况', summary: '观众聊日常', startMs: 40000, endMs: 55000, observations: '', evidence: [] },
    ],
  } }
}
async function router(path = '/analytics?sessionId=s1')
{
  const value = createRouter({ history: createMemoryHistory(), routes: [{ path: '/analytics', component: card }, { path: '/sessions/:id', component: card }] })
  await value.push(path)
  return value
}
async function mountView(path?: string, permissions?: string[])
{
  const navigation = await router(path)
  const pinia = createPinia()
  if (permissions)
  {
    useAuthStore(pinia).user = { id: 'viewer', email: 'viewer@example.test', displayName: '观众', role: 'User', status: 'Active', permissions }
  }
  const wrapper = mount(AnalyticsView, { global: { plugins: [pinia, i18n, navigation], stubs: { ...stubs, RelationshipCharts: true, ActivityChart: true, CategoryRevenueCard: true, RevenueComposition: true, TranslationAnalytics: true, UserRanking: true } } })
  mounted.push(wrapper)
  await flushPromises()
  return { wrapper, navigation }
}
async function openDetails(wrapper: VueWrapper)
{
  const actions = await openMenu(wrapper)
  await actions.findAll('[role="menuitem"]').find(item => item.text() === '分析详情')!.trigger('click')
  return wrapper.get('[role="dialog"]')
}
async function openMenu(wrapper: VueWrapper)
{
  await wrapper.get('.ai-analysis-toolbar button').trigger('click')
  return wrapper.get('[role="menu"]')
}
beforeEach(() =>
{
  vi.resetAllMocks()
  status.mockResolvedValue(result())
  liveMarkerStatus.mockResolvedValue({ ...result(), canGenerate: true, latestCompleted: null })
  sessions.mockResolvedValue([source()])
  streamers.mockResolvedValue([])
  analytics.mockResolvedValue(report())
  sessionDetail.mockImplementation(async (id: string) => ({ session: source(id), durationMs: 60000, segments: [], gaps: [] }))
  i18n.global.locale.value = 'zh-CN'
})
afterEach(() => { mounted.splice(0).forEach(wrapper => wrapper.unmount()); vi.useRealTimers() })

describe('统计页共享 AI 复盘', () =>
{
  it('菜单直播记录生成使用独立权限和接口，保留原话题结果与选区', async () =>
  {
    const markerJob = { ...job('Queued'), purpose: 'LiveMarkers' as const }
    generateLiveMarkers.mockResolvedValue(markerJob)
    const { wrapper } = await mountView(undefined, ['recordings.view', 'analytics.view', 'live-markers.generate'])
    expect(generateLiveMarkers).not.toHaveBeenCalled()
    const actions = await openMenu(wrapper)
    expect(actions.text()).not.toContain('重新分析')
    expect(actions.text()).toContain('生成直播记录')
    liveMarkerStatus.mockResolvedValue({ ...result(), canGenerate: true, currentJob: markerJob, latestCompleted: null })
    await actions.findAll('[role="menuitem"]').find(item => item.text() === '生成直播记录')!.trigger('click')
    await flushPromises()
    expect(generateLiveMarkers).toHaveBeenCalledWith('s1', false)
    expect(generate).not.toHaveBeenCalled()
    expect(wrapper.get('[role="dialog"]').text()).toContain('直播记录生成详情')
    expect(wrapper.get('.ai-analysis-results').text()).toContain('共享复盘')
    expect(wrapper.findAll('.topic-card')).toHaveLength(2)
  })

  it('顶部仅显示 AI分析按钮，菜单项分别触发生成和详情操作', async () =>
  {
    const wrapper = mount(AiAnalysisToolbar, { props: { canGenerate: true, canStart: true, hasResult: false }, global: { plugins: [i18n], stubs } })
    mounted.push(wrapper)
    expect(wrapper.findAll('button')).toHaveLength(1)
    expect(wrapper.get('button').text()).toBe('AI分析')
    expect(wrapper.find('[role="menu"]').exists()).toBe(false)
    const actions = await openMenu(wrapper)
    expect(actions.findAll('[role="menuitem"]').map(item => item.text())).toEqual(['生成分析', '分析详情'])
    expect(actions.text()).not.toContain('Token')
    await actions.findAll('[role="menuitem"]')[0]!.trigger('click')
    expect(wrapper.emitted('generate')).toEqual([[]])
    expect(wrapper.find('[role="menu"]').exists()).toBe(false)
    const reopened = await openMenu(wrapper)
    await reopened.findAll('[role="menuitem"]')[1]!.trigger('click')
    expect(wrapper.emitted('details')).toEqual([[]])
    expect(wrapper.find('[role="menu"]').exists()).toBe(false)
  })

  it('运行或忙碌时菜单禁用重新分析，但详情入口保持可用', async () =>
  {
    const wrapper = mount(AiAnalysisToolbar, { props: { canGenerate: true, canStart: false, hasResult: true }, global: { plugins: [i18n], stubs } })
    mounted.push(wrapper)
    const actions = await openMenu(wrapper)
    const items = actions.findAll('[role="menuitem"]')
    expect(items[0]!.text()).toBe('重新分析')
    expect(items[0]!.attributes('disabled')).toBeDefined()
    await items[0]!.trigger('click')
    expect(wrapper.emitted('generate')).toBeUndefined()
    expect(items[1]!.attributes('disabled')).toBeUndefined()
    await items[1]!.trigger('click')
    expect(wrapper.emitted('details')).toEqual([[]])
  })

  it('没有生成权限时菜单仅提供分析详情', async () =>
  {
    const wrapper = mount(AiAnalysisToolbar, { props: { canGenerate: false, canStart: false, hasResult: true }, global: { plugins: [i18n], stubs } })
    mounted.push(wrapper)
    const actions = await openMenu(wrapper)
    expect(actions.findAll('[role="menuitem"]').map(item => item.text())).toEqual(['分析详情'])
    await actions.get('[role="menuitem"]').trigger('click')
    expect(wrapper.emitted('details')).toEqual([[]])
    expect(wrapper.emitted('generate')).toBeUndefined()
  })

  it('安全显示共享依据，所有录像链接打开新标签页，查看无需生成权限', async () =>
  {
    const { wrapper } = await mountView()
    expect(wrapper.text()).toContain('共享复盘')
    expect(wrapper.text()).toContain('<script>ignored</script>')
    expect(wrapper.find('script').exists()).toBe(false)
    for (const link of wrapper.get('.ai-analysis-results').findAll('a'))
    {
      expect(link.attributes('target')).toBe('_blank')
      expect(link.attributes('rel')).toContain('noopener')
      expect(link.attributes('href')).toContain('/sessions/s1?at=')
    }
    expect(wrapper.get('.page-header').text()).not.toContain('重新分析')
    expect(generate).not.toHaveBeenCalled()
    expect(status).toHaveBeenCalledTimes(1)
  })

  it('从顶部工具栏生成，详情弹窗展示运行信息，关闭后用量和取消按钮不占据结果卡', async () =>
  {
    const initial = { ...result(), canGenerate: true, latestCompleted: null }
    const queued = { ...job('Queued'), requestRetries: 2 }
    status.mockResolvedValueOnce(initial).mockResolvedValue({ ...initial, currentJob: queued })
    generate.mockResolvedValue(queued)
    const { wrapper } = await mountView()
    expect(wrapper.get('.page-header .ai-analysis-toolbar').findAll('button')).toHaveLength(1)
    const actions = await openMenu(wrapper)
    const generateButton = actions.findAll('[role="menuitem"]').find(item => item.text() === '生成分析')!
    expect(wrapper.find('[role="dialog"]').exists()).toBe(false)
    expect(wrapper.get('.ai-analysis-results').findAll('button')).toHaveLength(0)
    await generateButton.trigger('click')
    await flushPromises()
    expect(generate).toHaveBeenCalledWith('s1', false)
    const details = wrapper.get('[role="dialog"]')
    expect(details.text()).toContain('Token 用量 35 · 参考 10,000')
    expect(details.text()).toContain('模型请求 1')
    expect(details.text()).toContain('工具调用 2')
    expect(details.text()).toContain('请求重试 2')
    expect(details.text()).toContain('排队中')
    expect(details.text()).toContain('进度 25%')
    expect(details.text()).toContain('取消分析')
    expect(details.find('[role="progressbar"]').exists()).toBe(true)
    expect(status).toHaveBeenCalledTimes(2)
    await details.findAll('button').find(button => button.text() === '关闭')!.trigger('click')
    expect(wrapper.text()).not.toContain('Token 用量')
    expect(wrapper.text()).not.toContain('模型请求')
    expect(wrapper.text()).not.toContain('工具调用')
    expect(wrapper.text()).not.toContain('请求重试')
    expect(wrapper.text()).not.toContain('排队中')
    expect(wrapper.text()).not.toContain('取消分析')
    expect(wrapper.find('[role="progressbar"]').exists()).toBe(false)
    await openDetails(wrapper)
    expect(status).toHaveBeenCalledTimes(2)
  })

  it('工具栏根据已有结果重新分析，详情内取消当前任务', async () =>
  {
    const initial = { ...result(), canGenerate: true }
    status.mockResolvedValueOnce(initial).mockResolvedValue({ ...initial, currentJob: { ...job(), progressRecoveries: 2 } })
    generate.mockResolvedValue(job('Queued'))
    cancel.mockResolvedValue(undefined)
    const { wrapper } = await mountView()
    const actions = await openMenu(wrapper)
    await actions.findAll('[role="menuitem"]').find(item => item.text() === '重新分析')!.trigger('click')
    await flushPromises()
    expect(generate).toHaveBeenCalledWith('s1', true)
    const details = wrapper.get('[role="dialog"]')
    expect(details.text()).toContain('分析分段')
    expect(details.text()).toContain('已完成 1 / 4 个分段')
    expect(details.text()).toContain('当前分段 2 · 00:15 — 00:30')
    expect(details.text()).toContain('上下文压缩 3')
    expect(details.text()).toContain('停滞恢复 2')
    expect(details.text()).not.toContain('请求重试')
    status.mockResolvedValue({ ...initial, currentJob: job('Cancelled') })
    await details.findAll('button').find(button => button.text() === '取消分析')!.trigger('click')
    await flushPromises()
    expect(cancel).toHaveBeenCalledWith('new')
    expect(details.text()).toContain('已取消')
    expect(details.text()).not.toContain('取消分析')
  })

  it('选中一个话题只更新统计选区，完整话题列表及图表话题标记保持不变', async () =>
  {
    vi.useFakeTimers()
    const { wrapper } = await mountView()
    await wrapper.get('.topic-card').findAll('button').find(button => button.text() === '查看此话题时段')!.trigger('click')
    await vi.advanceTimersByTimeAsync(250)
    await flushPromises()
    expect(analytics).toHaveBeenLastCalledWith('s1', { fromMs: 10000, toMs: 30000 }, expect.any(AbortSignal))
    expect(wrapper.findComponent(RelationshipCharts).props('range')).toEqual({ fromMs: 10000, toMs: 30000 })
    expect(wrapper.findAll('.topic-card')).toHaveLength(2)
    expect(wrapper.get('.ai-analysis-results').text()).toContain('分享近况')
    expect(wrapper.findComponent(RelationshipCharts).props('topics')).toHaveLength(2)
    expect(status).toHaveBeenCalledTimes(1)
    expect(generate).not.toHaveBeenCalled()
  })

  it('结果卡保留完整与部分切换及对应过期状态，运行错误只在详情中显示', async () =>
  {
    const data = result()
    data.latestPartial = { ...data.latestCompleted!, jobId: 'new', isComplete: false, overview: '当前规则的部分结果', topics: [] }
    data.currentJob = { ...job('Failed'), error: '模型暂时不可用' }
    data.latestCompletedStale = true
    data.latestPartialStale = false
    status.mockResolvedValue(data)
    const { wrapper } = await mountView()
    const panel = wrapper.get('.ai-analysis-results')
    expect(panel.text()).toContain('共享复盘')
    expect(panel.text()).toContain(i18n.global.t('replay.aiStale'))
    expect(wrapper.text()).not.toContain('模型暂时不可用')
    expect(panel.find('[role="status"]').exists()).toBe(false)
    await panel.findAll('button').find(button => button.text() === '本次部分结果')!.trigger('click')
    expect(panel.text()).toContain('当前规则的部分结果')
    expect(panel.text()).not.toContain(i18n.global.t('replay.aiStale'))
    const details = await openDetails(wrapper)
    expect(details.text()).toContain('模型暂时不可用')
    expect(details.text()).toContain('失败')
    expect(details.text()).not.toContain('取消分析')
  })

  it('普通查看者能查看运行详情但没有生成或取消入口', async () =>
  {
    status.mockResolvedValue({ ...result(), currentJob: job() })
    const { wrapper } = await mountView()
    const details = await openDetails(wrapper)
    expect(wrapper.get('.page-header').text()).not.toContain('重新分析')
    expect(details.text()).toContain('分析中')
    expect(details.text()).not.toContain('取消分析')
  })

  it('切换录像会关闭旧详情并丢弃迟到的分析读取', async () =>
  {
    let resolve!: (value: AiAnalysisStatus) => void
    status.mockReturnValueOnce(new Promise<AiAnalysisStatus>(accept => { resolve = accept }))
    status.mockResolvedValue({ ...result(), latestCompleted: { ...result().latestCompleted!, overview: '新录像的分析' } })
    const { wrapper, navigation } = await mountView('/analytics?sessionId=old')
    await openDetails(wrapper)
    await navigation.push('/analytics?sessionId=new')
    await flushPromises()
    resolve({ ...result(), latestCompleted: { ...result().latestCompleted!, overview: '旧录像的迟到响应' } })
    await flushPromises()
    expect(wrapper.find('[role="dialog"]').exists()).toBe(false)
    expect(wrapper.text()).toContain('新录像的分析')
    expect(wrapper.text()).not.toContain('旧录像的迟到响应')
  })

  it('关系图校验输入，总时长不跟随选区缩短', async () =>
  {
    const wrapper = mount(RelationshipCharts, { props: { sessionId: 's1', report: report(), durationMs: 60000 }, global: { plugins: [i18n, await router()], stubs } })
    mounted.push(wrapper)
    const inputs = wrapper.findAll('input')
    await wrapper.setProps({ durationMs: 60000.123, range: { fromMs: 10000.125, toMs: 60000.123 } })
    expect((inputs[0]!.element as HTMLInputElement).checkValidity()).toBe(true)
    expect((inputs[1]!.element as HTMLInputElement).checkValidity()).toBe(true)
    await inputs[0]!.setValue(20)
    await inputs[1]!.setValue(40)
    await wrapper.get('form').trigger('submit')
    expect(wrapper.emitted('range')?.[0]).toEqual([{ fromMs: 20000, toMs: 40000 }])
    await inputs[1]!.setValue(10)
    await wrapper.get('form').trigger('submit')
    expect(wrapper.find('[role="alert"]').exists()).toBe(true)
    expect(wrapper.emitted('range')).toHaveLength(1)
    expect(wrapper.find('a').exists()).toBe(false)
    expect(wrapper.get('.point-detail').text()).toContain('总时长 01:00')
  })
})
