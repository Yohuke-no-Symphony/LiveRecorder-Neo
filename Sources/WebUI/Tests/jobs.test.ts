// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type DOMWrapper, type VueWrapper } from '@vue/test-utils'
import { createPinia, disposePinia, setActivePinia, type Pinia } from 'pinia'
import { defineComponent, h, nextTick } from 'vue'
import { createMemoryHistory, createRouter } from 'vue-router'
import JobsView from '../src/views/JobsView.vue'
import WorkerTasksView from '../src/views/WorkerTasksView.vue'
import { useAuthStore } from '../src/stores/auth'
import type { TaskPage, TaskSummary } from '../src/models/taskHistory'
import type { UserProfile } from '../src/models/auth'
import type { WorkerLogEntry, WorkerLogPage } from '../src/models/transcoding'
import { i18n } from '../src/plugins/i18n'

const api = vi.hoisted(() => ({ list: vi.fn(), detail: vi.fn(), logs: vi.fn(), filters: vi.fn(), previewClear: vi.fn(), clearCompleted: vi.fn(), dismiss: vi.fn(), cancelMedia: vi.fn(), retryMedia: vi.fn(), cancelAi: vi.fn(), cancelTranscode: vi.fn(), retryTranscode: vi.fn(), download: vi.fn() }))
vi.mock('../src/apis/TaskHistoryAPI', () => ({ TaskHistoryAPI: api }))
vi.mock('../src/apis/MediaAPI', () => ({ MediaAPI: { cancelJob: api.cancelMedia, retryJob: api.retryMedia, downloadJobOutput: api.download } }))
vi.mock('../src/apis/AiAnalysisAPI', () => ({ AiAnalysisAPI: { cancel: api.cancelAi } }))
vi.mock('../src/apis/TranscodingAPI', () => ({ TranscodingAPI: { cancelJob: api.cancelTranscode, retryJob: api.retryTranscode } }))
const container = defineComponent({ setup: (_, { slots }) => () => h('div', slots.default?.()) })
const card = defineComponent({ props: ['title'], setup: (props, { slots }) => () => h('section', [props.title ? h('h2', props.title) : null, slots.default?.(), slots.actions?.(), slots['header-actions']?.()]) })
const button = defineComponent({ props: ['disabled', 'loading', 'href'], emits: ['click'], setup: (props, { slots, emit }) => () => h(props.href ? 'a' : 'button', { href: props.href, disabled: props.disabled || props.loading, onClick: () => emit('click') }, slots.default?.()) })
const select = defineComponent({ props: ['modelValue', 'label', 'items'], emits: ['update:modelValue'], setup: (props, { emit }) => () => h('select', { value: props.modelValue, 'aria-label': props.label, onChange: (event: Event) => emit('update:modelValue', (event.target as HTMLSelectElement).value) }, (props.items as { value: string; title: string }[]).map(item => h('option', { value: item.value }, item.title))) })
const input = defineComponent({ props: ['modelValue', 'label'], emits: ['update:modelValue'], setup: (props, { emit }) => () => h('input', { value: props.modelValue, 'aria-label': props.label, onInput: (event: Event) => emit('update:modelValue', (event.target as HTMLInputElement).value) }) })
const dialog = defineComponent({ props: ['modelValue', 'persistent'], setup: (props, { slots }) => () => props.modelValue ? h('div', { role: 'dialog' }, slots.default?.()) : null })
const table = defineComponent({ setup: (_, { slots }) => () => h('table', slots.default?.()) })
const tooltip = defineComponent({ props: ['text'], setup: (props, { slots }) => () => h('span', { 'data-tooltip': props.text }, slots.activator?.({ props: {} })) })
const stubs = { VBtn: button, VCard: card, ContentCard: card, VTable: table, VChip: container, VAvatar: container, VImg: true, VSelect: select, VTextField: input, VDialog: dialog, VProgressLinear: true, VProgressCircular: true, VAlert: container, VIcon: true, VTooltip: tooltip }
const mounted: VueWrapper[] = []
let pinia: Pinia
function viewer(role: 'Admin' | 'User' = 'User'): UserProfile { return { id: 'viewer', email: 'viewer@example.test', displayName: '观众', role, status: 'Active', permissions: ['recordings.view', 'clips.create', 'recordings.download', 'analytics.view'] } }
function task(overrides: Partial<TaskSummary> = {}): TaskSummary
{
  return { id: 'clip', kind: 'media', type: 'Clip', sessionId: 'session/id', title: '我的切片', state: 'Running', origin: 'User', initiatorUserId: 'viewer', initiatorName: '观众', initiatorAvatarUrl: '/avatars/user.png', workerId: 'worker', workerName: '节点甲', workerOnline: true, mode: 'Fast', progress: 30, attempt: 1, createdAtUtc: '2026-11-17T00:00:00Z', cancelRequested: false, canCancel: true, canRetry: false, canDelete: false, canViewResults: true, outputCount: 0, stages: [{ id: 'Downloading', state: 'Completed', progress: 100, metrics: [] }, { id: 'Encoding', state: 'Running', progress: 31, metrics: [{ key: 'framesPerSecond', value: 83.5, unit: 'fps' }, { key: 'promptTokens', value: 900, unit: 'number' }] }], ...overrides }
}
const page = (items: TaskSummary[], nextCursor: string | null = null, total = items.length): TaskPage => ({ items, nextCursor, total })
const log = (id: string, message: string): WorkerLogEntry => ({ id, message, workerId: 'worker', streamId: 'stream', sequence: 1, cursor: 1, timestampUtc: '2026-11-17T00:00:00Z', receivedAtUtc: '2026-11-17T00:00:00Z', level: 'Information' })
function deferred<T>() { let resolve!: (value: T) => void; let reject!: (error: Error) => void; const promise = new Promise<T>((accept, fail) => { resolve = accept; reject = fail }); return { promise, resolve, reject } }
async function open(administration = false, query = '')
{
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/jobs', component: container }, { path: '/admin/worker-tasks', component: container }] })
  await router.push(`${administration ? '/admin/worker-tasks' : '/jobs'}${query}`)
  const wrapper = mount(administration ? WorkerTasksView : JobsView, { global: { plugins: [pinia, i18n, router], stubs } })
  mounted.push(wrapper)
  await flushPromises()
  return wrapper
}
function findButton(wrapper: Pick<DOMWrapper<Element>, 'findAll'> | VueWrapper, label: string)
{
  const result = wrapper.findAll('button').find(item => item.attributes('aria-label') === label || item.text() === label)
  if (!result) { throw new Error(`Missing button ${label}`) }
  return result
}
beforeEach(() =>
{
  vi.resetAllMocks()
  vi.useFakeTimers()
  vi.setSystemTime(new Date('2026-11-17T00:01:00Z'))
  vi.stubGlobal('localStorage', { getItem: () => null, setItem: vi.fn(), removeItem: vi.fn() })
  pinia = createPinia(); setActivePinia(pinia)
  useAuthStore().user = viewer()
  i18n.global.locale.value = 'zh-CN'
  api.list.mockResolvedValue(page([task()]))
  api.filters.mockResolvedValue({ initiators: [{ id: 'creator', name: '创建者乙' }], workers: [{ id: 'worker', name: '节点甲' }] })
  api.previewClear.mockResolvedValue({ count: 77, completedBeforeUtc: '2026-11-17T00:00:59Z' })
  api.clearCompleted.mockResolvedValue({ removed: 77 })
  api.download.mockImplementation((id, index) => `/api/jobs/${id}/outputs/${index}`)
  api.logs.mockResolvedValue({ entries: [], nextCursor: null, hasMore: false })
})
afterEach(() => { mounted.splice(0).forEach(wrapper => wrapper.unmount()); disposePinia(pinia); vi.useRealTimers(); vi.unstubAllGlobals(); i18n.global.locale.value = 'zh-CN' })

describe('统一任务表格', () =>
{
  it('个人页默认全部并仅调用统一的权限分页接口，表格保留创建人和紧凑badge', async () =>
  {
    const wrapper = await open()
    expect(api.list).toHaveBeenCalledWith('personal', expect.objectContaining({ state: 'all', limit: 50 }), expect.any(AbortSignal))
    expect(api.filters).not.toHaveBeenCalled()
    expect(wrapper.get('h1').text()).toBe('后台任务')
    const row = wrapper.get('[data-job-id="clip"]')
    expect(row.text()).toContain('观众')
    expect(row.text()).toContain('节点甲')
    expect(row.text()).toContain('00:01:00')
    expect(row.text()).not.toContain('83.5 FPS')
    expect(row.text()).not.toContain('900')
  })

  it('点击Stage才展示真实指标，切片Encoding显示导出并提供进度条', async () =>
  {
    const wrapper = await open()
    await wrapper.get('[data-stage-id="Encoding"]').trigger('click')
    const modal = wrapper.get('[role="dialog"]')
    expect(modal.get('h2').text()).toBe('导出')
    expect(modal.text()).toContain('83.5 FPS')
    expect(modal.text()).toContain('900')
    expect(modal.find('v-progress-linear-stub').exists()).toBe(true)
    await findButton(modal, '关闭').trigger('click')
    expect(wrapper.find('[role="dialog"]').exists()).toBe(false)
  })

  it('列表仅显示带圆环进度和tooltip的阶段图，弹窗在图下显示名称并支持切换', async () =>
  {
    const wrapper = await open()
    const row = wrapper.get('[data-job-id="clip"]')
    expect(row.find('.stage-node-name').exists()).toBe(false)
    expect(row.find('.current-stages').exists()).toBe(false)
    expect(row.find('v-progress-linear-stub').exists()).toBe(false)
    expect(row.get('[data-stage-id="Encoding"] v-progress-circular-stub').attributes('model-value')).toBe('31')
    expect(row.get('[data-stage-id="Encoding"]').attributes('aria-label')).toContain('导出 · 运行中 · 31%')
    expect(row.get('[data-stage-id="Encoding"]').element.parentElement?.getAttribute('data-tooltip')).toContain('导出 · 运行中 · 31%')
    await row.get('[data-stage-id="Encoding"]').trigger('click')
    const modal = wrapper.get('[role="dialog"]')
    expect(modal.get('[data-stage-id="Encoding"]').attributes('aria-pressed')).toBe('true')
    const downloadNode = modal.get('[data-stage-id="Downloading"]')
    expect(downloadNode.text()).not.toContain('下载')
    const downloadName = modal.findAll('.stage-node-name').find(value => value.text() === '下载')!
    await downloadName.trigger('click')
    expect(modal.get('h2').text()).toBe('导出')
    expect(modal.get('[data-stage-id="Downloading"] v-progress-circular-stub').attributes('model-value')).toBe('100')
    await modal.get('[data-stage-id="Downloading"]').trigger('click')
    expect(modal.get('h2').text()).toBe('下载')
    expect(modal.get('[data-stage-id="Downloading"]').attributes('aria-pressed')).toBe('true')
    expect(modal.text()).not.toContain('83.5 FPS')
    await findButton(wrapper, '刷新').trigger('click'); await flushPromises()
    expect(modal.get('h2').text()).toBe('下载')
  })

  it('管理员展开当前阶段日志，切换步骤中止旧请求且不混入迟到响应', async () =>
  {
    useAuthStore().user = viewer('Admin')
    const pending = deferred<WorkerLogPage>()
    api.logs.mockReturnValueOnce(pending.promise).mockResolvedValue({ entries: [log('download-log', '下载阶段日志')], nextCursor: 5, hasMore: false })
    const wrapper = await open(true)
    await wrapper.get('[data-stage-id="Encoding"]').trigger('click')
    expect(api.logs).not.toHaveBeenCalled()
    const modal = wrapper.get('[role="dialog"]')
    await findButton(modal, '查看日志').trigger('click'); await flushPromises()
    expect(api.logs).toHaveBeenCalledWith('administration', expect.objectContaining({ id: 'clip', kind: 'media' }), 'Encoding', undefined, expect.any(AbortSignal))
    const oldSignal = api.logs.mock.calls[0]![4] as AbortSignal
    await modal.get('[data-stage-id="Downloading"]').trigger('click'); await flushPromises()
    expect(oldSignal.aborted).toBe(true)
    expect(api.logs.mock.calls.at(-1)![2]).toBe('Downloading')
    pending.resolve({ entries: [log('late-log', '迟到的编码日志')], nextCursor: 2, hasMore: false }); await flushPromises()
    expect(modal.get('[role="log"]').text()).toContain('下载阶段日志')
    expect(modal.get('[role="log"]').text()).not.toContain('迟到的编码日志')
    await vi.advanceTimersByTimeAsync(1000); await flushPromises()
    expect(api.logs.mock.calls.at(-1)![3]).toBe(5)
    await findButton(modal, '关闭').trigger('click')
    const count = api.logs.mock.calls.length
    await vi.advanceTimersByTimeAsync(1000); await flushPromises()
    expect(api.logs).toHaveBeenCalledTimes(count)
  })

  it('日志沿用个人页查询范围，历史录像日志标明来源并提供明确空态', async () =>
  {
    useAuthStore().user = viewer('Admin')
    api.logs.mockResolvedValueOnce({ entries: [{ ...log('history', '连续 5 分钟无有效媒体，结束当前场次。'), source: 'SessionSnapshot' }], nextCursor: null, hasMore: false })
    const wrapper = await open()
    await wrapper.get('[data-stage-id="Encoding"]').trigger('click')
    const modal = wrapper.get('[role="dialog"]')
    await findButton(modal, '查看日志').trigger('click'); await flushPromises()
    expect(api.logs.mock.calls[0]![0]).toBe('personal')
    expect(modal.get('[role="log"]').text()).toContain('历史录像记录')
    await modal.get('[data-stage-id="Downloading"]').trigger('click'); await flushPromises()
    expect(modal.get('[role="log"]').text()).toContain('没有此阶段的日志')
  })

  it('普通用户不显示原始技术日志入口', async () =>
  {
    const wrapper = await open()
    await wrapper.get('[data-stage-id="Encoding"]').trigger('click')
    expect(wrapper.get('[role="dialog"]').text()).not.toContain('查看日志')
    expect(api.logs).not.toHaveBeenCalled()
  })

  it('成功的采集和上传步骤不把任务残留超时当作当前错误', async () =>
  {
    useAuthStore().user = viewer('Admin')
    api.list.mockResolvedValue(page([task({ kind: 'recording', type: 'Recording', state: 'Completed', error: 'RECORDING_STREAM_TIMEOUT', stages: [
      { id: 'Capturing', state: 'Completed', progress: 100, metrics: [] },
      { id: 'Uploading', state: 'Completed', progress: 100, error: 'RECORDING_STREAM_TIMEOUT', metrics: [] },
    ] })]))
    const wrapper = await open(true)
    await wrapper.get('[data-stage-id="Capturing"]').trigger('click')
    const modal = wrapper.get('[role="dialog"]')
    expect(modal.text()).not.toContain('RECORDING_STREAM_TIMEOUT')
    await modal.get('[data-stage-id="Uploading"]').trigger('click')
    expect(modal.get('h2').text()).toBe('上传')
    expect(modal.text()).not.toContain('RECORDING_STREAM_TIMEOUT')
  })

  it('运行阶段重试显示警告，失败阶段仍显示其错误', async () =>
  {
    const current = task({ state: 'Failed', error: '失败诊断', stages: [
      { id: 'Downloading', state: 'Running', progress: 50, error: '等待重试', metrics: [] },
      { id: 'Encoding', state: 'Failed', progress: 10, metrics: [] },
    ] })
    api.list.mockResolvedValue(page([current]))
    const wrapper = await open()
    await wrapper.get('[data-stage-id="Downloading"]').trigger('click')
    const modal = wrapper.get('[role="dialog"]')
    expect(modal.get('[type="warning"]').text()).toBe('等待重试')
    expect(modal.text()).not.toContain('失败诊断')
    await modal.get('[data-stage-id="Encoding"]').trigger('click')
    expect(modal.get('[type="error"]').text()).toBe('失败诊断')
  })

  it('服务器cursor控制上下页，改变筛选重置cursor而非本地截取', async () =>
  {
    api.list.mockResolvedValueOnce(page([task()], 'next-token', 123)).mockResolvedValueOnce(page([task({ id: 'page2', title: '下一页任务' })], null, 123)).mockResolvedValue(page([task({ id: 'filtered' })], null, 7))
    const wrapper = await open()
    await findButton(wrapper, '下一页').trigger('click'); await flushPromises()
    expect(api.list.mock.calls.at(-1)![1].cursor).toBe('next-token')
    expect(wrapper.find('[data-job-id="page2"]').exists()).toBe(true)
    expect(wrapper.find('[data-job-id="clip"]').exists()).toBe(false)
    await wrapper.get('select[aria-label="状态"]').setValue('active'); await flushPromises()
    expect(api.list.mock.calls.at(-1)![1]).toEqual(expect.objectContaining({ state: 'active', cursor: undefined }))
    expect(findButton(wrapper, '上一页').attributes('disabled')).toBeDefined()
  })

  it('搜索防抖300ms，并将查询交给服务器', async () =>
  {
    const wrapper = await open()
    const calls = api.list.mock.calls.length
    await wrapper.get('input[aria-label="搜索任务"]').setValue('  录播  ')
    await vi.advanceTimersByTimeAsync(299)
    expect(api.list).toHaveBeenCalledTimes(calls)
    await vi.advanceTimersByTimeAsync(1); await flushPromises()
    expect(api.list.mock.calls.at(-1)![1].search).toBe('录播')
  })

  it('管理员从统一接口读取并提供来源、Worker和创建人筛选及系统标记', async () =>
  {
    useAuthStore().user = viewer('Admin')
    api.list.mockResolvedValue(page([task({ origin: 'Automatic', initiatorName: null, initiatorUserId: null })]))
    const wrapper = await open(true, '?workerId=worker')
    expect(api.filters).toHaveBeenCalledTimes(1)
    expect(api.list.mock.calls[0]![0]).toBe('administration')
    expect(api.list.mock.calls[0]![1].workerId).toBe('worker')
    expect(wrapper.text()).toContain('系统')
    expect(wrapper.get('select[aria-label="创建人"]').text()).toContain('创建者乙')
    await wrapper.get('select[aria-label="来源"]').setValue('Automatic'); await flushPromises()
    await wrapper.get('select[aria-label="创建人"]').setValue('creator'); await flushPromises()
    expect(api.list.mock.calls.at(-1)![1]).toEqual(expect.objectContaining({ origin: 'Automatic', initiatorUserId: 'creator' }))
  })

  it('管理员个人页可筛选自己创建的转码修复合并，录像仅在管理页出现', async () =>
  {
    useAuthStore().user = viewer('Admin')
    const wrapper = await open()
    const types = wrapper.get('select[aria-label="类型"]')
    expect(types.text()).toContain('转码')
    expect(types.text()).toContain('修复')
    expect(types.text()).toContain('合并')
    expect(types.find('option[value="Recording"]').exists()).toBe(false)
    await types.setValue('Merge'); await flushPromises()
    expect(api.list.mock.calls.at(-1)![0]).toBe('personal')
    expect(api.list.mock.calls.at(-1)![1].type).toBe('Merge')
  })

  it('Worker卡片链接的状态和节点query作为初始服务端筛选', async () =>
  {
    useAuthStore().user = viewer('Admin')
    const wrapper = await open(true, '?workerId=worker&state=active')
    expect(api.list.mock.calls[0]![1]).toEqual(expect.objectContaining({ state: 'active', workerId: 'worker', limit: 50 }))
    expect((wrapper.get('select[aria-label="状态"]').element as HTMLSelectElement).value).toBe('active')
  })

  it('取消和重试使用各类型接口，权限标志为false时不暴露操作', async () =>
  {
    api.list.mockResolvedValue(page([task(), task({ id: 'transcode', kind: 'transcode', type: 'Transcode', state: 'Failed', canCancel: false, canRetry: true }), task({ id: 'read-only', canCancel: false, canRetry: false })]))
    const wrapper = await open()
    await findButton(wrapper.get('[data-job-id="clip"]'), '取消').trigger('click'); await flushPromises()
    expect(api.cancelMedia).toHaveBeenCalledWith('clip')
    await findButton(wrapper.get('[data-job-id="transcode"]'), '重试').trigger('click'); await flushPromises()
    expect(api.retryTranscode).toHaveBeenCalledWith('transcode')
    expect(wrapper.get('[data-job-id="read-only"]').findAll('button.job-action-button')).toHaveLength(0)
  })

  it('删除记录必须确认，取消确认不调用接口，成功请求保留attempt终态凭据', async () =>
  {
    const completed = task({ state: 'Completed', canCancel: false, canDelete: true, finishedAtUtc: '2026-11-17T00:00:59Z' })
    api.list.mockResolvedValue(page([completed]))
    const wrapper = await open()
    await findButton(wrapper.get('[data-job-id="clip"]'), '删除记录').trigger('click')
    expect(api.dismiss).not.toHaveBeenCalled()
    await findButton(wrapper.get('[role="dialog"]'), '取消').trigger('click')
    expect(api.dismiss).not.toHaveBeenCalled()
    await findButton(wrapper.get('[data-job-id="clip"]'), '删除记录').trigger('click')
    expect(wrapper.get('[role="dialog"]').text()).toContain('已生成的录像和文件会保留')
    await findButton(wrapper.get('[role="dialog"]'), '删除记录').trigger('click'); await flushPromises()
    expect(api.dismiss).toHaveBeenCalledWith('personal', expect.objectContaining({ attempt: 1, finishedAtUtc: completed.finishedAtUtc }))
  })

  it('删除失败仅在确认框显示一次错误，重试期间禁止关闭和重复确认', async () =>
  {
    api.list.mockResolvedValue(page([task({ state: 'Completed', canDelete: true, canCancel: false, finishedAtUtc: '2026-11-17T00:00:59Z' })]))
    api.dismiss.mockRejectedValueOnce(new Error('记录状态已变化'))
    const wrapper = await open()
    await findButton(wrapper.get('[data-job-id="clip"]'), '删除记录').trigger('click')
    await findButton(wrapper.get('[role="dialog"]'), '删除记录').trigger('click'); await flushPromises()
    expect(wrapper.text().match(/记录状态已变化/g)).toHaveLength(1)
    const pending = deferred<void>()
    api.dismiss.mockReturnValueOnce(pending.promise)
    await findButton(wrapper.get('[role="dialog"]'), '删除记录').trigger('click')
    const activeDialog = wrapper.findAllComponents(dialog).find(component => component.props('modelValue'))!
    expect(activeDialog.props('persistent')).toBe(true)
    expect(findButton(wrapper.get('[role="dialog"]'), '取消').attributes('disabled')).toBeDefined()
    pending.resolve(); await flushPromises()
  })

  it('清空使用全部分页预览数量，并冻结筛选和完成时间截止点', async () =>
  {
    const wrapper = await open()
    await wrapper.get('select[aria-label="类型"]').setValue('Clip'); await flushPromises()
    await findButton(wrapper, '清空已完成').trigger('click'); await flushPromises()
    expect(api.clearCompleted).not.toHaveBeenCalled()
    const modal = wrapper.get('[role="dialog"]')
    expect(modal.text()).toContain('77')
    expect(modal.text()).toContain('全部分页')
    await wrapper.get('select[aria-label="类型"]').setValue('Analysis'); await flushPromises()
    await findButton(modal, '清空已完成').trigger('click'); await flushPromises()
    expect(api.clearCompleted).toHaveBeenCalledWith('personal', expect.objectContaining({ type: 'Clip' }), '2026-11-17T00:00:59Z')
  })

  it('预览没有已完成项时给出结果而不出现确认或发送删除', async () =>
  {
    api.previewClear.mockResolvedValue({ count: 0, completedBeforeUtc: '2026-11-17T00:00:59Z' })
    const wrapper = await open()
    await findButton(wrapper, '清空已完成').trigger('click'); await flushPromises()
    expect(wrapper.text()).toContain('没有可清空')
    expect(wrapper.find('[role="dialog"]').exists()).toBe(false)
    expect(api.clearCompleted).not.toHaveBeenCalled()
  })

  it('并行刷新只采纳新请求，旧响应和权限撤销不恢复管理员数据或模态框', async () =>
  {
    useAuthStore().user = viewer('Admin')
    const wrapper = await open(true)
    await wrapper.get('[data-stage-id="Encoding"]').trigger('click')
    const pending = deferred<TaskPage>()
    api.list.mockReturnValueOnce(pending.promise)
    await findButton(wrapper, '刷新').trigger('click')
    const signal = api.list.mock.calls.at(-1)![2] as AbortSignal
    useAuthStore().user = viewer()
    await nextTick()
    expect(signal.aborted).toBe(true)
    expect(wrapper.find('[data-job-id="clip"]').exists()).toBe(false)
    expect(wrapper.find('[role="dialog"]').exists()).toBe(false)
    pending.resolve(page([task({ title: '不应重新出现的管理员任务' })])); await flushPromises()
    expect(wrapper.text()).not.toContain('不应重新出现')
  })

  it('关闭Stage后，轮询中迟到的详情响应不能重开modal', async () =>
  {
    const wrapper = await open()
    await wrapper.get('[data-stage-id="Encoding"]').trigger('click')
    const pending = deferred<TaskSummary>()
    api.list.mockResolvedValue(page([task({ id: 'other' })]))
    api.detail.mockReturnValueOnce(pending.promise)
    await findButton(wrapper, '刷新').trigger('click'); await flushPromises()
    await findButton(wrapper.get('[role="dialog"]'), '关闭').trigger('click')
    pending.resolve(task()); await flushPromises()
    expect(wrapper.find('[role="dialog"]').exists()).toBe(false)
  })

  it('保留Merge多文件下载和AI结果链接，错误只进入详情', async () =>
  {
    api.list.mockResolvedValue(page([task({ id: 'merge', type: 'Merge', state: 'Completed', canCancel: false, outputCount: 2 }), task({ id: 'ai', kind: 'ai', type: 'Analysis', error: '详情错误' })]))
    const wrapper = await open()
    expect(wrapper.get('a[aria-label="下载文件 1"]').attributes('href')).toBe('/api/jobs/merge/outputs/0')
    expect(wrapper.get('a[aria-label="下载文件 2"]').attributes('href')).toBe('/api/jobs/merge/outputs/1')
    expect(wrapper.get('a[aria-label="查看分析"]').attributes('href')).toBe('/analytics?sessionId=session%2Fid')
    expect(wrapper.text()).not.toContain('详情错误')
  })

  it('历史直播记录结果链接保留该任务产物的文件ID并编码空格和斜线', async () =>
  {
    api.list.mockResolvedValue(page([task({ id: 'markers', kind: 'ai', type: 'LiveMarkers', state: 'Completed', canCancel: false, liveMarkerFileId: 'file /1' })]))
    const wrapper = await open()
    const link = wrapper.get('a[aria-label="查看直播记录"]')
    const href = link.attributes('href')!
    expect(href).toContain('liveMarkerFileId=file%20%2F1')
    const url = new URL(href, 'https://recorder.test')
    expect(url.pathname).toBe('/sessions/session%2Fid')
    expect(url.searchParams.get('panel')).toBe('markers')
    expect(url.searchParams.get('liveMarkerFileId')).toBe('file /1')
    expect(link.attributes('target')).toBe('_blank')
    expect(link.attributes('rel')).toContain('noopener')
  })

  it('三语动态更新步骤、筛选与已打开的详情标题', async () =>
  {
    const wrapper = await open()
    await wrapper.get('[data-stage-id="Encoding"]').trigger('click')
    for (const [locale, title] of [['en-US', 'Exporting'], ['ja-JP', '書き出し'], ['zh-CN', '导出']] as const)
    {
      i18n.global.locale.value = locale; await nextTick()
      expect(wrapper.get('[role="dialog"] h2').text()).toBe(title)
      expect(wrapper.text()).not.toContain('taskHistory.')
    }
  })

  it('录像上传追平仍保持运行状态，点击节点说明采集继续而非整场完成', async () =>
  {
    useAuthStore().user = viewer('Admin')
    api.list.mockResolvedValue(page([task({ kind: 'recording', type: 'Recording', origin: 'Automatic', stages: [
      { id: 'Capturing', state: 'Running', progress: null, metrics: [] },
      { id: 'Uploading', state: 'Running', progress: 100, substate: 'CaughtUp', metrics: [{ key: 'acknowledgedBytes', value: 1024, unit: 'bytes' }] },
    ] })]))
    const wrapper = await open(true)
    expect(wrapper.get('[data-stage-id="Uploading"]').attributes('data-stage-state')).toBe('Running')
    await wrapper.get('[data-stage-id="Uploading"]').trigger('click')
    expect(wrapper.get('[role="dialog"]').text()).toContain('已同步，采集继续')
    expect(wrapper.get('[role="dialog"]').text()).toContain('1 KB')
  })
})
