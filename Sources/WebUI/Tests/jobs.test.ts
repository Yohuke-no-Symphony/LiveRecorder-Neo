// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type DOMWrapper, type VueWrapper } from '@vue/test-utils'
import { createPinia, disposePinia, setActivePinia, type Pinia } from 'pinia'
import { defineComponent, h, nextTick } from 'vue'
import { createMemoryHistory, createRouter } from 'vue-router'
import JobsView from '../src/views/JobsView.vue'
import { useAuthStore } from '../src/stores/auth'
import type { AiAnalysisJob } from '../src/models/aiAnalysis'
import type { MediaJob } from '../src/models/media'
import type { UserProfile } from '../src/models/auth'
import { i18n } from '../src/plugins/i18n'

const { transcodeJobs, cancelTranscode, retryTranscode, mediaJobs, aiJobs, cancelMedia, retryMedia, cancelAi, downloadJobOutput } = vi.hoisted(() => ({ transcodeJobs: vi.fn(), cancelTranscode: vi.fn(), retryTranscode: vi.fn(), mediaJobs: vi.fn(), aiJobs: vi.fn(), cancelMedia: vi.fn(), retryMedia: vi.fn(), cancelAi: vi.fn(), downloadJobOutput: vi.fn() }))
vi.mock('../src/apis/TranscodingAPI', () => ({ TranscodingAPI: { jobs: transcodeJobs, cancelJob: cancelTranscode, retryJob: retryTranscode } }))
vi.mock('../src/apis/MediaAPI', () => ({ MediaAPI: { jobs: mediaJobs, cancelJob: cancelMedia, retryJob: retryMedia, downloadJobOutput } }))
vi.mock('../src/apis/AiAnalysisAPI', () => ({ AiAnalysisAPI: { jobs: aiJobs, cancel: cancelAi } }))
const card = defineComponent({ props: ['title'], setup: (props, { slots }) => () => h('section', [props.title ? h('h2', props.title) : null, slots.default?.(), slots.actions?.()]) })
const button = defineComponent({ props: ['disabled', 'loading', 'href'], emits: ['click'], setup: (props, { slots, emit }) => () => h(props.href ? 'a' : 'button', { href: props.href, disabled: props.disabled || props.loading, onClick: () => emit('click') }, slots.default?.()) })
const group = defineComponent({ props: ['modelValue'], emits: ['update:modelValue'], setup: (props, { emit }) => () => h('select', { 'aria-label': '任务筛选', value: props.modelValue, onChange: (event: Event) => emit('update:modelValue', (event.target as HTMLSelectElement).value) }, ['all', 'active', 'failed'].map(value => h('option', { value }, value))) })
const dialog = defineComponent({ props: ['modelValue'], setup: (props, { slots }) => () => props.modelValue ? h('div', { role: 'dialog' }, slots.default?.()) : null })
const table = defineComponent({ setup: (_, { slots }) => () => h('table', slots.default?.()) })
const stubs = { VBtn: button, VCard: card, ContentCard: card, VTable: table, VChip: card, VChipGroup: group, VDialog: dialog, VProgressLinear: true, VAlert: card, VIcon: true }
const mounted: VueWrapper[] = []
let pinia: Pinia

function viewer(permissions = ['recordings.view', 'analytics.view', 'analytics.generate']): UserProfile
{
  return { id: 'viewer', email: 'viewer@example.test', displayName: '观众', role: 'User', status: 'Active', permissions }
}
function media(id = 'media', state = 'Running'): MediaJob
{
  return { id, createdAtUtc: '2026-11-14T00:01:00Z', ownerId: 'viewer', kind: 'Clip', sessionId: 'session', title: '我的切片任务', mode: 'Fast', startMs: 0, endMs: 60000, segmentIds: [], outputRelativePaths: [], state, progress: 30, attempt: 0, cancelRequested: false }
}
function ai(id = 'ai', state: AiAnalysisJob['state'] = 'Running'): AiAnalysisJob
{
  return { id, sessionId: 'shared/session', title: '全局录像复盘', cancelRequested: false, state, progress: 45, phase: 'Analyzing', totalSegments: 4, completedSegments: 1, currentSegment: 2, currentSegmentStartMs: 15000, currentSegmentEndMs: 30000, contextCompactions: 2, promptTokens: 100, completionTokens: 20, estimatedTokens: 5, tokenBudget: 1000000, modelRequests: 3, toolCalls: 4, errorCode: null, createdAtUtc: '2026-11-14T00:02:00Z', finishedAtUtc: null }
}
function deferred<T>()
{
  let resolve!: (value: T) => void
  let reject!: (reason: Error) => void
  const promise = new Promise<T>((accept, fail) => { resolve = accept; reject = fail })
  return { promise, resolve, reject }
}
async function mountView()
{
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/jobs', component: card }, { path: '/sessions/:id', component: card }, { path: '/analytics', component: card }] })
  await router.push('/jobs')
  const wrapper = mount(JobsView, { global: { plugins: [pinia, i18n, router], stubs } })
  mounted.push(wrapper)
  await flushPromises()
  return wrapper
}
function findButton(wrapper: Pick<DOMWrapper<Element>, 'findAll'>, label: string)
{
  const result = wrapper.findAll('button').find(item => item.text() === label)
  if (!result)
  {
    throw new Error(`Missing button: ${label}`)
  }
  return result
}
beforeEach(() =>
{
  vi.resetAllMocks()
  vi.stubGlobal('localStorage', { getItem: () => null, setItem: vi.fn(), removeItem: vi.fn() })
  pinia = createPinia()
  setActivePinia(pinia)
  useAuthStore().user = viewer()
  i18n.global.locale.value = 'zh-CN'
  transcodeJobs.mockResolvedValue([])
  cancelTranscode.mockResolvedValue(undefined)
  retryTranscode.mockResolvedValue(undefined)
  mediaJobs.mockResolvedValue([media()])
  aiJobs.mockResolvedValue([ai()])
  cancelMedia.mockResolvedValue(undefined)
  retryMedia.mockResolvedValue(undefined)
  cancelAi.mockResolvedValue(undefined)
  downloadJobOutput.mockImplementation((id: string, index: number) => `/api/jobs/${id}/outputs/${index}`)
})
afterEach(() =>
{
  mounted.splice(0).forEach(wrapper => wrapper.unmount())
  disposePinia(pinia)
  vi.useRealTimers()
  vi.unstubAllGlobals()
})

describe('后台任务中的 AI 分析', () =>
{
  it('只有录像观看权限也读取全局直播记录任务，并过滤无权查看的话题任务', async () =>
  {
    useAuthStore().user = viewer(['recordings.view'])
    aiJobs.mockResolvedValue([ai('analysis'), { ...ai('markers', 'Completed'), purpose: 'LiveMarkers', liveMarkerFileId: 'file /1' }])
    const wrapper = await mountView()
    expect(aiJobs).toHaveBeenCalledTimes(1)
    expect(wrapper.find('[data-job-id="analysis"]').exists()).toBe(false)
    const row = wrapper.get('[data-job-id="markers"]')
    expect(row.text()).toContain('生成直播记录')
    expect(row.text()).not.toContain('取消')
    const link = row.findAll('a').find(value => value.text() === '查看直播记录')!
    const url = new URL(link.attributes('href')!, 'https://recorder.test')
    expect(url.pathname).toBe('/sessions/shared%2Fsession')
    expect(url.searchParams.get('panel')).toBe('markers')
    expect(url.searchParams.get('liveMarkerFileId')).toBe('file /1')
    expect(link.attributes('target')).toBe('_blank')
    await findButton(row, '直播记录生成详情').trigger('click')
    expect(wrapper.get('[role="dialog"]').text()).toContain('直播记录生成详情')
  })

  it.each([
    ['analytics.generate', 'analysis', 'markers'],
    ['live-markers.generate', 'markers', 'analysis'],
  ])('权限 %s 仅能取消对应目的作业', async (permission, allowed, denied) =>
  {
    useAuthStore().user = viewer(['recordings.view', 'analytics.view', permission])
    aiJobs.mockResolvedValue([ai('analysis'), { ...ai('markers'), purpose: 'LiveMarkers' }])
    const wrapper = await mountView()
    expect(wrapper.get(`[data-job-id="${denied}"]`).text()).not.toContain('取消')
    await findButton(wrapper.get(`[data-job-id="${allowed}"]`), '取消').trigger('click')
    await flushPromises()
    expect(cancelAi).toHaveBeenCalledWith(allowed)
    expect(cancelMedia).not.toHaveBeenCalled()
  })

  it('直播记录发布中禁用行内和详情取消', async () =>
  {
    useAuthStore().user = viewer(['recordings.view', 'live-markers.generate'])
    aiJobs.mockResolvedValue([{ ...ai('publishing'), purpose: 'LiveMarkers', phase: 'Publishing' }])
    const wrapper = await mountView()
    const row = wrapper.get('[data-job-id="publishing"]')
    expect(findButton(row, '取消').attributes('disabled')).toBeDefined()
    await findButton(row, '直播记录生成详情').trigger('click')
    const details = wrapper.get('[role="dialog"]')
    expect(details.text()).toContain('发布中')
    expect(findButton(details, '取消生成').attributes('disabled')).toBeDefined()
    expect(cancelAi).not.toHaveBeenCalled()
  })

  it('合并全局 AI 和媒体作业并按时间排序，详情外不显示 token 用量', async () =>
  {
    mediaJobs.mockResolvedValue([media('same-id')])
    aiJobs.mockResolvedValue([ai('same-id')])
    const wrapper = await mountView()
    const rows = wrapper.findAll('tbody tr')
    expect(rows).toHaveLength(2)
    expect(rows[0]!.attributes('data-job-kind')).toBe('ai')
    expect(rows[0]!.text()).toContain('全局录像复盘')
    expect(rows[0]!.text()).toContain('AI话题分析')
    expect(rows[0]!.text()).toContain('45%')
    expect(rows[1]!.text()).toContain('我的切片任务')
    expect(wrapper.text()).not.toContain('Token')
    const resultLink = rows[0]!.findAll('a').find(link => link.text() === '查看分析')!
    expect(resultLink.attributes('href')).toBe('/analytics?sessionId=shared/session')
    expect(resultLink.attributes('target')).toBe('_blank')
    expect(resultLink.attributes('rel')).toContain('noopener')
    await findButton(rows[0]!, '分析详情').trigger('click')
    const details = wrapper.get('[role="dialog"]')
    expect(details.text()).toContain('Token 用量 125 · 参考 1,000,000')
    expect(details.text()).toContain('上下文压缩 2')
    expect(details.text()).toContain('全局录像复盘')
    expect(aiJobs).toHaveBeenCalledTimes(1)
  })

  it.each([['analytics.view'], []])('权限 %j 不请求 AI 接口，仍正常加载媒体作业', async (...permissions) =>
  {
    useAuthStore().user = viewer(permissions)
    const wrapper = await mountView()
    expect(aiJobs).not.toHaveBeenCalled()
    expect(mediaJobs).toHaveBeenCalledTimes(1)
    expect(wrapper.findAll('tbody tr')).toHaveLength(1)
    expect(wrapper.text()).toContain('我的切片任务')
  })

  it('进行中与未完成筛选包含 AI 分段任务及取消、中断和部分结果', async () =>
  {
    mediaJobs.mockResolvedValue([])
    aiJobs.mockResolvedValue(['Queued', 'Running', 'Completed', 'Partial', 'Failed', 'Interrupted', 'Cancelled'].map(state => ai(state, state as AiAnalysisJob['state'])))
    const wrapper = await mountView()
    await wrapper.get('select').setValue('active')
    expect(wrapper.findAll('tbody tr').map(row => row.attributes('data-job-id'))).toEqual(['Queued', 'Running'])
    await wrapper.get('select').setValue('failed')
    expect(wrapper.findAll('tbody tr').map(row => row.attributes('data-job-id'))).toEqual(['Partial', 'Failed', 'Interrupted', 'Cancelled'])
    expect(wrapper.text()).toContain('部分完成')
  })

  it('普通查看者能查看全局详情但不能取消 AI，且不会出现媒体重试入口', async () =>
  {
    useAuthStore().user = viewer(['recordings.view', 'analytics.view'])
    aiJobs.mockResolvedValue([ai(), ai('failed', 'Failed')])
    const wrapper = await mountView()
    for (const row of wrapper.findAll('[data-job-kind="ai"]'))
    {
      expect(row.text()).not.toContain('取消')
      expect(row.text()).not.toContain('重试')
    }
    await findButton(wrapper.get('[data-job-id="ai"]'), '分析详情').trigger('click')
    expect(wrapper.get('[role="dialog"]').text()).not.toContain('取消分析')
    expect(cancelAi).not.toHaveBeenCalled()
    expect(retryMedia).not.toHaveBeenCalled()
  })

  it('AI 取消使用独立接口，取消请求发出后禁用重复取消', async () =>
  {
    const wrapper = await mountView()
    aiJobs.mockResolvedValue([{ ...ai(), cancelRequested: true }])
    await findButton(wrapper.get('[data-job-kind="ai"]'), '取消').trigger('click')
    await flushPromises()
    expect(cancelAi).toHaveBeenCalledWith('ai')
    expect(cancelMedia).not.toHaveBeenCalled()
    const cancelling = findButton(wrapper.get('[data-job-kind="ai"]'), '取消中')
    expect(cancelling.attributes('disabled')).toBeDefined()
    await findButton(wrapper.get('[data-job-kind="ai"]'), '分析详情').trigger('click')
    expect(findButton(wrapper.get('[role="dialog"]'), '取消中').attributes('disabled')).toBeDefined()
  })

  it('AI 接口失败时媒体作业仍可用，刷新可恢复 AI 列表', async () =>
  {
    aiJobs.mockRejectedValueOnce(new Error('AI 列表暂不可用'))
    const wrapper = await mountView()
    expect(wrapper.text()).toContain('我的切片任务')
    expect(wrapper.text()).toContain('AI 列表暂不可用')
    await findButton(wrapper, '刷新').trigger('click')
    await flushPromises()
    expect(wrapper.text()).toContain('全局录像复盘')
    expect(wrapper.text()).not.toContain('AI 列表暂不可用')
  })

  it('AI 列表响应缓慢时先显示媒体作业，离开页面会取消等待中的读取', async () =>
  {
    const pending = deferred<AiAnalysisJob[]>()
    aiJobs.mockReturnValueOnce(pending.promise)
    const wrapper = await mountView()
    expect(wrapper.text()).toContain('我的切片任务')
    const signal = aiJobs.mock.calls[0]![0] as AbortSignal
    wrapper.unmount()
    expect(signal.aborted).toBe(true)
    pending.resolve([ai()])
    await flushPromises()
    expect(aiJobs).toHaveBeenCalledTimes(1)
  })

  it('权限撤销会立即关闭详情并清空 AI 行，恢复权限后丢弃撤销前迟到响应', async () =>
  {
    const auth = useAuthStore()
    const wrapper = await mountView()
    await findButton(wrapper.get('[data-job-kind="ai"]'), '分析详情').trigger('click')
    const pending = deferred<AiAnalysisJob[]>()
    aiJobs.mockReturnValueOnce(pending.promise)
    await findButton(wrapper, '刷新').trigger('click')
    const signal = aiJobs.mock.calls.at(-1)![0] as AbortSignal
    auth.user = viewer(['recordings.view'])
    await nextTick()
    expect(wrapper.find('[data-job-kind="ai"]').exists()).toBe(false)
    expect(wrapper.find('[role="dialog"]').exists()).toBe(false)
    expect(signal.aborted).toBe(true)
    await flushPromises()
    aiJobs.mockResolvedValue([{ ...ai('fresh'), title: '恢复权限后的列表' }])
    auth.user = viewer()
    await flushPromises()
    pending.resolve([{ ...ai('stale'), title: '撤销前的旧列表' }])
    await flushPromises()
    expect(wrapper.text()).toContain('恢复权限后的列表')
    expect(wrapper.text()).not.toContain('撤销前的旧列表')
    expect(wrapper.find('[role="dialog"]').exists()).toBe(false)
  })

  it('手动刷新与轮询重叠时只采纳最新请求结果', async () =>
  {
    vi.useFakeTimers()
    const wrapper = await mountView()
    const pending = deferred<AiAnalysisJob[]>()
    aiJobs.mockReturnValueOnce(pending.promise).mockResolvedValue([{ ...ai('new'), title: '最新任务' }])
    await findButton(wrapper, '刷新').trigger('click')
    await vi.advanceTimersByTimeAsync(4000)
    await flushPromises()
    pending.resolve([{ ...ai('old'), title: '旧任务响应' }])
    await flushPromises()
    expect(wrapper.text()).toContain('最新任务')
    expect(wrapper.text()).not.toContain('旧任务响应')
  })

  it('身份切换后旧取消错误不覆盖新用户任务', async () =>
  {
    const auth = useAuthStore()
    const wrapper = await mountView()
    const pending = deferred<void>()
    cancelAi.mockReturnValueOnce(pending.promise)
    await findButton(wrapper.get('[data-job-kind="ai"]'), '取消').trigger('click')
    auth.user = { ...viewer(), id: 'other-viewer' }
    await flushPromises()
    const reads = aiJobs.mock.calls.length
    pending.reject(new Error('旧用户取消失败'))
    await flushPromises()
    expect(wrapper.text()).not.toContain('旧用户取消失败')
    expect(aiJobs).toHaveBeenCalledTimes(reads)
  })

  it('保留媒体作业的重试和下载行为，AI 历史详情对应选中作业', async () =>
  {
    useAuthStore().user = { ...viewer(), role: 'Admin' }
    mediaJobs.mockResolvedValue([{ ...media('repair', 'Completed'), kind: 'Repair', outputRelativePaths: ['file.mp4'] }, media('clip', 'Failed')])
    aiJobs.mockResolvedValue([ai('new', 'Completed'), { ...ai('old', 'Failed'), createdAtUtc: '2026-11-14T00:00:00Z', promptTokens: 900, completionTokens: 0, estimatedTokens: 0, error: '历史任务错误' }])
    const wrapper = await mountView()
    expect(wrapper.get('[data-job-id="repair"] a[href="/api/jobs/repair/outputs/0"]').text()).toBe('下载文件')
    await findButton(wrapper.get('[data-job-id="clip"]'), '重试').trigger('click')
    await flushPromises()
    expect(retryMedia).toHaveBeenCalledWith('clip')
    await findButton(wrapper.get('[data-job-id="old"]'), '分析详情').trigger('click')
    expect(wrapper.get('[role="dialog"]').text()).toContain('Token 用量 900')
    expect(wrapper.get('[role="dialog"]').text()).toContain('历史任务错误')
    expect(wrapper.get('[data-job-id="old"]').text()).not.toContain('历史任务错误')
  })
})


describe('后台转码任务', () =>
{
  it('下载显示阶段完成量、速率和剩余时间，并区分整体进度', async () =>
  {
    useAuthStore().user = { ...viewer(), role: 'Admin' }
    transcodeJobs.mockResolvedValue([{ id: 'download', sessionId: 'session', title: '正在下载的录像', profileName: '720p', state: 'Running', stage: 'Downloading', progress: 6, workerId: 'worker', activity: { bytesTransferred: 60 * 1024 ** 2, totalBytes: 100 * 1024 ** 2, bytesPerSecond: 8 * 1024 ** 2, estimatedSecondsRemaining: 5, currentFile: 'source.flv' }, lastProgressAtUtc: '2026-11-15T12:17:42Z', createdAtUtc: '2026-11-15T12:00:00Z' }])
    const wrapper = await mountView()
    const row = wrapper.get('[data-job-id="download"]')
    expect(row.text()).toContain('6% 总进度')
    expect(row.text()).toContain('阶段进度 60%')
    expect(row.text()).toContain('60 MB / 100 MB')
    expect(row.text()).toContain('8 MB/s')
    expect(row.text()).toContain('预计剩余 00:00:05')
    expect(row.text()).toContain('source.flv')
    expect(row.text()).toContain('最近上报')
    expect(row.text()).toMatch(/\d{1,2}:\d{2}:42/)
  })

  it('转码展示媒体时长、帧率、倍率和编码器，完成后隐藏即时速率', async () =>
  {
    useAuthStore().user = { ...viewer(), role: 'Admin' }
    const job = { id: 'encoding', sessionId: 'session', title: '正在转码的录像', profileName: '720p', state: 'Running', stage: 'Encoding', progress: 34.5, encoder: 'h264_videotoolbox', activity: { processedDurationMs: 180000, totalDurationMs: 600000, framesPerSecond: 83.5, speed: 2.4, estimatedSecondsRemaining: 175 }, createdAtUtc: '2026-11-15T12:00:00Z' }
    transcodeJobs.mockResolvedValue([job])
    const wrapper = await mountView()
    const row = wrapper.get('[data-job-id="encoding"]')
    expect(row.text()).toContain('阶段进度 30%')
    expect(row.text()).toContain('00:03:00 / 00:10:00')
    expect(row.text()).toContain('83.5 FPS')
    expect(row.text()).toContain('2.4×')
    expect(row.text()).toContain('预计剩余 00:02:55')
    expect(row.text()).toContain('编码器 h264_videotoolbox')
    transcodeJobs.mockResolvedValue([{ ...job, state: 'Completed', stage: 'Completed', progress: 100 }])
    await findButton(wrapper, '刷新').trigger('click')
    await flushPromises()
    expect(row.text()).toContain('100% 总进度')
    expect(row.text()).not.toContain('FPS')
    expect(row.text()).not.toContain('预计剩余')
  })

  it('上传显示文件数量与当前文件，等待发布时清除阶段指标', async () =>
  {
    useAuthStore().user = { ...viewer(), role: 'Admin' }
    const job = { id: 'uploading', sessionId: 'session', title: '正在上传的录像', profileName: '720p', state: 'Running', stage: 'Uploading', progress: 92, activity: { bytesTransferred: 50 * 1024 ** 2, totalBytes: 100 * 1024 ** 2, filesCompleted: 125, filesTotal: 602, currentFile: 'chunk_00125.m4s' }, createdAtUtc: '2026-11-15T12:00:00Z' }
    transcodeJobs.mockResolvedValue([job])
    const wrapper = await mountView()
    const row = wrapper.get('[data-job-id="uploading"]')
    expect(row.text()).toContain('阶段进度 50%')
    expect(row.text()).toContain('文件 125 / 602')
    expect(row.text()).toContain('chunk_00125.m4s')
    transcodeJobs.mockResolvedValue([{ ...job, stage: 'Finalizing', progress: 99 }])
    await findButton(wrapper, '刷新').trigger('click')
    await flushPromises()
    expect(row.text()).toContain('等待发布')
    expect(row.text()).not.toContain('阶段进度')
    expect(row.text()).not.toContain('50 MB')
    expect(row.text()).not.toContain('chunk_00125.m4s')
  })

  it('旧节点缺少指标时显示原有进度，新阶段随网页语言更新', async () =>
  {
    useAuthStore().user = { ...viewer(), role: 'Admin' }
    transcodeJobs.mockResolvedValue([{ id: 'legacy', sessionId: 'session', title: '旧节点录像', profileName: '720p', state: 'Running', stage: 'Inspecting', progress: 10, createdAtUtc: '2026-11-15T12:00:00Z' }])
    const wrapper = await mountView()
    const row = wrapper.get('[data-job-id="legacy"]')
    expect(row.text()).toContain('检查原片')
    expect(row.text()).toContain('10% 总进度')
    expect(row.text()).not.toContain('阶段进度')
    expect(row.text()).not.toContain('undefined')
    i18n.global.locale.value = 'en-US'
    await nextTick()
    expect(row.text()).toContain('Inspecting input')
    expect(row.text()).toContain('10% overall')
    i18n.global.locale.value = 'ja-JP'
    await nextTick()
    expect(row.text()).toContain('入力を確認中')
    expect(row.text()).toContain('全体 10%')
  })

  it('仅管理员读取转码作业，与其他作业同ID也各自显示，取消走独立接口', async () =>
  {
    useAuthStore().user = { ...viewer(), role: 'Admin' }
    transcodeJobs.mockResolvedValue([{ id: 'media', sessionId: 'session', title: '转码录播', profileName: '720p', state: 'Running', stage: 'Encoding', progress: 50, workerId: 'worker', createdAtUtc: '2026-11-14T00:03:00Z' }])
    const wrapper = await mountView()
    expect(transcodeJobs).toHaveBeenCalledTimes(1)
    const row = wrapper.get('[data-job-kind="transcode"]')
    expect(row.text()).toContain('720p')
    expect(row.text()).toContain('编码中')
    await findButton(row, '取消').trigger('click')
    await flushPromises()
    expect(cancelTranscode).toHaveBeenCalledWith('media')
    expect(cancelMedia).not.toHaveBeenCalled()
    expect(wrapper.find('[data-job-kind="media"]').exists()).toBe(true)
  })

  it('降为普通用户后清除转码作业并忽略已发出的过期响应', async () =>
  {
    useAuthStore().user = { ...viewer(), role: 'Admin' }
    const pending = deferred<unknown[]>()
    transcodeJobs.mockReturnValueOnce(pending.promise)
    const wrapper = await mountView()
    useAuthStore().user = viewer()
    pending.resolve([{ id: 'private', sessionId: 'secret', title: '管理员转码', profileName: '720p', state: 'Queued', stage: 'WaitingForWorker', progress: 0, createdAtUtc: '2026-11-14T00:03:00Z' }])
    await flushPromises()
    expect(wrapper.find('[data-job-kind="transcode"]').exists()).toBe(false)
    expect(transcodeJobs).toHaveBeenCalledTimes(1)
  })

  it('普通用户不请求管理员转码接口', async () =>
  {
    await mountView()
    expect(transcodeJobs).not.toHaveBeenCalled()
  })
})
