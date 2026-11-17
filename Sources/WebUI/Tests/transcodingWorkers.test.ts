// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { defineComponent, h } from 'vue'
import { createPinia, disposePinia, setActivePinia, type Pinia } from 'pinia'
import TranscodingWorkersPanel from '../src/components/common/TranscodingWorkersPanel.vue'
import StatusChip from '../src/components/common/StatusChip.vue'
import { TranscodingAPI } from '../src/apis/TranscodingAPI'
import { TaskHistoryAPI } from '../src/apis/TaskHistoryAPI'
import type { TaskSummary } from '../src/models/taskHistory'
import { defaultWorkerConfiguration, type ClippingWorker, type RecordingWorker, type TranscodingWorker, type WorkerKind } from '../src/models/transcoding'
import { i18n } from '../src/plugins/i18n'

vi.mock('../src/apis/TranscodingAPI', () => ({ TranscodingAPI: { workers: vi.fn(), createWorker: vi.fn(), updateWorker: vi.fn(), rotateWorkerKey: vi.fn(), deleteWorker: vi.fn() } }))
vi.mock('../src/apis/TaskHistoryAPI', () => ({ TaskHistoryAPI: { active: vi.fn(), detail: vi.fn() } }))
const field = defineComponent({ props: ['modelValue', 'label', 'disabled', 'readonly'], emits: ['update:modelValue'], setup: (props, { emit }) => () => h('input', { value: props.modelValue, 'aria-label': props.label, disabled: props.disabled, readonly: props.readonly, onInput: (event: Event) => emit('update:modelValue', (event.target as HTMLInputElement).value) }) })
const stubs = {
  ContentCard: { props: ['title'], template: '<section><h2>{{ title }}</h2><slot name="header-actions" /><slot /><slot name="actions" /></section>' },
  VBtn: { props: ['disabled', 'loading'], template: '<button :disabled="disabled || loading"><slot /></button>' },
  VTooltip: { template: '<slot name="activator" :props="{}" />' },
  VTextField: field, VTextarea: field, VSelect: field,
  VSwitch: { props: ['modelValue', 'label', 'disabled'], emits: ['update:modelValue'], template: '<input type="checkbox" :aria-label="label" :checked="modelValue" :disabled="disabled" @change="$emit(\'update:modelValue\', $event.target.checked)" />' },
  VDialog: { props: ['modelValue'], template: '<div v-if="modelValue" role="dialog"><slot /></div>' },
  VAlert: { template: '<div role="alert"><slot /></div>' }, VSpacer: true, VIcon: true, VProgressLinear: true, VChip: { template: '<span><slot /></span>' },
}
const mounted: VueWrapper[] = []
let worker: TranscodingWorker
let pinia: Pinia

async function open(kind?: WorkerKind): Promise<VueWrapper>
{
  const wrapper = mount(TranscodingWorkersPanel, { props: { kind }, attachTo: document.body, global: { plugins: [pinia, i18n], stubs } })
  mounted.push(wrapper)
  await flushPromises()
  return wrapper
}
function button(wrapper: Pick<VueWrapper, 'findAll'>, label: string)
{
  const item = wrapper.findAll('button').find(item => item.text() === label)
  if (!item) { throw new Error(`Missing ${label}`) }
  return item
}
beforeEach(() =>
{
  vi.resetAllMocks()
  vi.useFakeTimers()
  pinia = createPinia(); setActivePinia(pinia)
  i18n.global.locale.value = 'zh-CN'
  worker = { id: 'node-1', name: '本地 Worker', enabled: true, configuration: defaultWorkerConfiguration(), configurationRevision: 4, appliedConfigurationRevision: 3, configurationError: null, capabilities: { operatingSystem: 'Darwin', architecture: 'Arm64', toolVersion: '7', encoders: [{ encoder: 'h264_videotoolbox', deviceId: null, available: true }] }, telemetry: { cpuPercent: null, cpuThreadBudget: 4, runningGpu: 1, runningCpu: 0, freeBytes: 1024 ** 3 }, lastSeenAtUtc: new Date().toISOString() }
  vi.mocked(TranscodingAPI.workers).mockResolvedValue([worker])
  vi.mocked(TaskHistoryAPI.active).mockResolvedValue({})
  vi.mocked(TranscodingAPI.createWorker).mockResolvedValue({ worker, secret: 'one-time-key' })
  vi.mocked(TranscodingAPI.updateWorker).mockResolvedValue(worker)
  vi.mocked(TranscodingAPI.rotateWorkerKey).mockResolvedValue({ secret: 'rotated-key' })
})
afterEach(() => { mounted.splice(0).forEach(wrapper => wrapper.unmount()); disposePinia(pinia); document.body.innerHTML = ''; vi.useRealTimers() })

describe('Worker管理', () =>
{
  it('全部节点一次批量读取活动任务，每卡最多3条并共用步骤详情', async () =>
  {
    vi.mocked(TranscodingAPI.workers).mockResolvedValue([worker, { ...worker, id: 'node-2', name: '节点乙' }])
    const task = (id: string): TaskSummary => ({ id, kind: 'transcode', type: 'Transcode', sessionId: 'session', title: `转码${id}`, state: 'Running', origin: 'Automatic', progress: 20, attempt: 1, createdAtUtc: '2026-11-17T00:00:00Z', cancelRequested: false, canCancel: true, canRetry: false, canDelete: false, canViewResults: true, outputCount: 0, stages: [{ id: 'Encoding', state: 'Running', progress: 20, metrics: [{ key: 'framesPerSecond', value: 80, unit: 'fps' }] }] })
    vi.mocked(TaskHistoryAPI.active).mockResolvedValue({ 'node-1': [task('1'), task('2'), task('3'), task('4')], 'node-2': [task('5')] })

    const wrapper = await open()

    expect(TaskHistoryAPI.active).toHaveBeenCalledTimes(1)
    expect(TaskHistoryAPI.active).toHaveBeenCalledWith(['node-1', 'node-2'], expect.any(AbortSignal))
    expect(wrapper.findAll('.worker-row')[0]!.findAll('[data-task-id]')).toHaveLength(3)
    expect(wrapper.findAll('.worker-row')[1]!.findAll('[data-task-id]')).toHaveLength(1)
    expect(wrapper.get('a[href="/admin/worker-tasks?workerId=node-1&state=active"]').text()).toContain('查看全部')
    expect(wrapper.text()).not.toContain('80 FPS')
    await wrapper.get('[data-task-id="1"] [data-stage-id="Encoding"]').trigger('click')
    expect(wrapper.get('[role="dialog"]').text()).toContain('80 FPS')
    expect(wrapper.get('[role="dialog"]').text()).toContain('编码')
  })

  it('卸载面板取消批量任务请求，迟到的活动任务不会重新显示', async () =>
  {
    let resolve!: (value: Record<string, TaskSummary[]>) => void
    vi.mocked(TaskHistoryAPI.active).mockReturnValueOnce(new Promise(done => { resolve = done }))
    const wrapper = await open()
    const signal = vi.mocked(TaskHistoryAPI.active).mock.calls[0]![1]!
    wrapper.unmount()
    expect(signal.aborted).toBe(true)
    resolve({ 'node-1': [] })
    await flushPromises()
    expect(TaskHistoryAPI.active).toHaveBeenCalledTimes(1)
  })

  it('三类节点独立采用主站在线值，停止一个节点与客户端时钟不会改变其他节点状态', async () =>
  {
    const transcodingWorker = { ...worker, isOnline: true, lastSeenAtUtc: '2000-01-01T00:00:00Z' }
    const clippingWorker: ClippingWorker = { ...worker, id: 'clip-1', kind: 'Clipping', configuration: defaultWorkerConfiguration('Clipping'), isOnline: false, lastSeenAtUtc: new Date().toISOString() }
    const recordingWorker: RecordingWorker = { ...worker, id: 'recording-1', kind: 'Recording', configuration: defaultWorkerConfiguration('Recording'), isOnline: true, lastSeenAtUtc: null }
    vi.mocked(TranscodingAPI.workers).mockResolvedValue([transcodingWorker, clippingWorker, recordingWorker])
    vi.setSystemTime(new Date('2035-01-01T00:00:00Z'))
    const transcoding = await open()
    const clipping = await open('Clipping')
    const recording = await open('Recording')
    expect(transcoding.getComponent(StatusChip).props('status')).toBe('online')
    expect(clipping.getComponent(StatusChip).props('status')).toBe('disconnected')
    expect(recording.getComponent(StatusChip).props('status')).toBe('online')
    expect(transcoding.text()).toContain('在线')
    expect(clipping.text()).toContain('离线')
    expect(transcoding.text()).not.toContain('有效')
    vi.setSystemTime(new Date('1990-01-01T00:00:00Z'))
    await clipping.get('button[aria-label="刷新"]').trigger('click')
    await flushPromises()
    expect(clipping.getComponent(StatusChip).props('status')).toBe('disconnected')
    expect(transcoding.getComponent(StatusChip).props('status')).toBe('online')
    expect(recording.getComponent(StatusChip).props('status')).toBe('online')
  })

  it('旧API缺少在线值时显示离线，停用节点保留禁用状态', async () =>
  {
    const wrapper = await open()
    expect(wrapper.getComponent(StatusChip).props('status')).toBe('disconnected')
    vi.mocked(TranscodingAPI.workers).mockResolvedValue([{ ...worker, enabled: false, isOnline: true }])
    await wrapper.get('button[aria-label="刷新"]').trigger('click')
    await flushPromises()
    expect(wrapper.getComponent(StatusChip).props('status')).toBe('disabled')
  })

  it('显示实际能力与配置待应用状态，创建仅提交名称并一次展示密钥', async () =>
  {
    const wrapper = await open()
    expect(wrapper.text()).toContain('h264_videotoolbox')
    expect(wrapper.text()).toContain('配置等待 Worker 应用')
    expect(wrapper.find('input[aria-label="名称"]').exists()).toBe(false)
    await wrapper.get('button[aria-label="添加 Worker"]').trigger('click')
    expect(TranscodingAPI.createWorker).not.toHaveBeenCalled()
    await wrapper.get('input[aria-label="名称"]').setValue(' 新节点 ')
    await button(wrapper, '添加 Worker').trigger('click')
    await flushPromises()
    expect(TranscodingAPI.createWorker).toHaveBeenCalledWith('新节点')
    expect(wrapper.find('input[aria-label="名称"]').exists()).toBe(false)
    expect(wrapper.get('input[aria-label="访问密钥"]').element).toHaveProperty('value', 'one-time-key')
    await button(wrapper, '关闭').trigger('click')
    expect(wrapper.find('input[aria-label="访问密钥"]').exists()).toBe(false)
    expect(TranscodingAPI.updateWorker).not.toHaveBeenCalled()
  })

  it('空列表仅显示标题栏创建入口，取消名称弹窗不创建 Worker', async () =>
  {
    vi.mocked(TranscodingAPI.workers).mockResolvedValue([])
    const wrapper = await open()
    expect(wrapper.find('input[aria-label="名称"]').exists()).toBe(false)
    const actions = wrapper.findAll('button')
    expect(actions.map(item => item.attributes('aria-label'))).toEqual(['添加 Worker', '刷新'])
    await actions[0]!.trigger('click')
    await wrapper.get('input[aria-label="名称"]').setValue('新节点')
    await button(wrapper, '取消').trigger('click')
    expect(wrapper.find('[role="dialog"]').exists()).toBe(false)
    expect(TranscodingAPI.createWorker).not.toHaveBeenCalled()
  })

  it('编辑独立草稿，带expectedRevision保存，失败保持输入供纠正', async () =>
  {
    Object.assign(worker.configuration, { clipEncodingThreads: 99 })
    const wrapper = await open()
    await button(wrapper, '编辑 Worker').trigger('click')
    const dialog = wrapper.get('[role="dialog"]')
    await dialog.get('input[aria-label="总并发任务数"]').setValue('3')
    expect(worker.configuration.maxConcurrentEncodes).toBe(2)
    expect(TranscodingAPI.updateWorker).not.toHaveBeenCalled()
    vi.mocked(TranscodingAPI.updateWorker).mockRejectedValueOnce(new Error('配置已更新，请刷新'))
    await button(wrapper, '保存 Worker').trigger('click')
    await flushPromises()
    expect(dialog.text()).toContain('配置已更新，请刷新')
    expect(dialog.get('input[aria-label="总并发任务数"]').element).toHaveProperty('value', '3')
    expect(TranscodingAPI.updateWorker).toHaveBeenCalledWith('node-1', expect.objectContaining({ expectedRevision: 4, configuration: expect.objectContaining({ maxConcurrentEncodes: 3, cpuFallbackMode: 'Balanced' }) }))
    expect(dialog.find('input[aria-label="精确切片 CPU 编码线程数"]').exists()).toBe(false)
    expect(vi.mocked(TranscodingAPI.updateWorker).mock.calls[0]![1].configuration).not.toHaveProperty('clipEncodingThreads')
  })

  it('单位换算为字节并拒绝无效资源数，保存期间不能修改', async () =>
  {
    const wrapper = await open()
    await button(wrapper, '编辑 Worker').trigger('click')
    const dialog = wrapper.get('[role="dialog"]')
    await dialog.get('input[aria-label="下载上限（MiB/s）"]').setValue('1.1')
    await dialog.get('input[aria-label="CPU 负载平均窗口（秒）"]').setValue('1')
    await button(wrapper, '保存 Worker').trigger('click')
    expect(TranscodingAPI.updateWorker).not.toHaveBeenCalled()
    await dialog.get('input[aria-label="CPU 负载平均窗口（秒）"]').setValue('30')
    let finish!: (value: TranscodingWorker) => void
    vi.mocked(TranscodingAPI.updateWorker).mockReturnValueOnce(new Promise(resolve => { finish = resolve }))
    await button(wrapper, '保存 Worker').trigger('click')
    expect(dialog.findAll('input').every(input => input.attributes('disabled') !== undefined)).toBe(true)
    expect(TranscodingAPI.updateWorker).toHaveBeenCalledWith('node-1', expect.objectContaining({ configuration: expect.objectContaining({ downloadBytesPerSecond: 1153434 }) }))
    finish(worker)
    await flushPromises()
    expect(wrapper.find('[role="dialog"]').exists()).toBe(false)
  })

  it('轮换前显示确认，失败留在确认框且不显示虚构密钥', async () =>
  {
    const wrapper = await open()
    await button(wrapper, '轮换密钥').trigger('click')
    expect(TranscodingAPI.rotateWorkerKey).not.toHaveBeenCalled()
    vi.mocked(TranscodingAPI.rotateWorkerKey).mockRejectedValueOnce(new Error('轮换失败'))
    const dialog = wrapper.get('[role="dialog"]')
    const confirm = dialog.findAll('button').find(item => item.text() === '轮换密钥')!
    await confirm.trigger('click')
    await flushPromises()
    expect(dialog.text()).toContain('轮换失败')
    expect(wrapper.find('input[aria-label="访问密钥"]').exists()).toBe(false)
  })

  it('卸载后取消轮询和在途读取，迟到响应不恢复界面', async () =>
  {
    const wrapper = await open()
    const signal = vi.mocked(TranscodingAPI.workers).mock.calls[0]![0]!
    wrapper.unmount()
    mounted.splice(mounted.indexOf(wrapper), 1)
    expect(signal.aborted).toBe(true)
    await vi.advanceTimersByTimeAsync(10000)
    expect(TranscodingAPI.workers).toHaveBeenCalledTimes(1)
  })

  it('按节点类型显示列表，切片节点使用独立身份创建并复用一次性密钥交付', async () =>
  {
    const clippingWorker: ClippingWorker = { ...worker, id: 'clip-1', name: '切片节点', kind: 'Clipping', configuration: defaultWorkerConfiguration('Clipping') }
    vi.mocked(TranscodingAPI.workers).mockResolvedValue([worker, clippingWorker])
    const transcoding = await open()
    expect(transcoding.text()).toContain('本地 Worker')
    expect(transcoding.text()).not.toContain('切片节点')
    const clipping = await open('Clipping')
    expect(clipping.text()).toContain('切片 Worker')
    expect(clipping.text()).toContain('切片节点')
    expect(clipping.text()).not.toContain('本地 Worker')
    expect(clipping.text()).not.toContain('h264_videotoolbox')
    expect(clipping.text()).toContain('运行任务 1')
    vi.mocked(TranscodingAPI.createWorker).mockResolvedValueOnce({ worker: clippingWorker, secret: 'clip-access-key' })
    await clipping.get('button[aria-label="添加 Worker"]').trigger('click')
    await clipping.get('input[aria-label="名称"]').setValue(' 切片节点 ')
    await clipping.get('form').trigger('submit')
    await flushPromises()
    expect(TranscodingAPI.createWorker).toHaveBeenCalledWith('切片节点', 'Clipping')
    expect(clipping.get('input[aria-label="访问密钥"]').element).toHaveProperty('value', 'clip-access-key')
  })

  it('切片配置使用任务、空间和传输限额，隐藏的转码资源参数不阻止保存', async () =>
  {
    const clippingWorker: ClippingWorker = {
      ...worker, kind: 'Clipping',
      configuration: { ...worker.configuration, clipEncodingThreads: 2 },
    }
    vi.mocked(TranscodingAPI.workers).mockResolvedValue([clippingWorker])
    const wrapper = await open('Clipping')
    await button(wrapper, '编辑 Worker').trigger('click')
    const dialog = wrapper.get('[role="dialog"]')
    expect(dialog.find('input[aria-label="首选编码器"]').exists()).toBe(false)
    expect(dialog.find('input[aria-label="GPU 并发任务数"]').exists()).toBe(false)
    expect(dialog.find('input[aria-label="CPU 负载平均窗口（秒）"]').exists()).toBe(false)
    expect(dialog.find('input[aria-label="允许 CPU 回退"]').exists()).toBe(false)
    await dialog.get('input[aria-label="临时空间上限（GiB）"]').setValue('4.5')
    await dialog.get('input[aria-label="并发切片任务数"]').setValue('3')
    await dialog.get('input[aria-label="精确切片 CPU 编码线程数"]').setValue('6')
    await dialog.get('form').trigger('submit')
    await flushPromises()
    expect(TranscodingAPI.updateWorker).toHaveBeenCalledWith('node-1', expect.objectContaining({
      expectedRevision: 4,
      configuration: {
        ffmpegPath: 'ffmpeg', ffprobePath: 'ffprobe', tempDirectory: '', maxConcurrentEncodes: 3,
        clipEncodingThreads: 6, maxCacheBytes: 4831838208, minimumFreeBytes: 2 * 1024 ** 3,
        downloadBytesPerSecond: 0, uploadBytesPerSecond: 0,
      },
    }))
    expect(wrapper.find('[role="dialog"]').exists()).toBe(false)
  })

  it('切片读取精简配置并补齐旧配置线程默认值，拒绝越界和小数线程数', async () =>
  {
    const clippingWorker: ClippingWorker = {
      ...worker, kind: 'Clipping', configuration: { ...defaultWorkerConfiguration('Clipping'), tempDirectory: '/srv/clips' },
    }
    Reflect.deleteProperty(clippingWorker.configuration, 'clipEncodingThreads')
    vi.mocked(TranscodingAPI.workers).mockResolvedValue([clippingWorker])
    const wrapper = await open('Clipping')
    await button(wrapper, '编辑 Worker').trigger('click')
    const threads = wrapper.get('input[aria-label="精确切片 CPU 编码线程数"]')
    expect(threads.element).toHaveProperty('value', '2')
    for (const invalid of ['0', '129', '1.5'])
    {
      await threads.setValue(invalid)
      await wrapper.get('form').trigger('submit')
      expect(TranscodingAPI.updateWorker).not.toHaveBeenCalled()
    }
    await threads.setValue('128')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(TranscodingAPI.updateWorker).toHaveBeenCalledWith('node-1', expect.objectContaining({
      configuration: { ...clippingWorker.configuration, clipEncodingThreads: 128 },
    }))
  })

  it('切片配置在三语切换后更新标题和临时空间标签并保留草稿', async () =>
  {
    const clippingWorker: ClippingWorker = { ...worker, kind: 'Clipping', configuration: defaultWorkerConfiguration('Clipping') }
    vi.mocked(TranscodingAPI.workers).mockResolvedValue([clippingWorker])
    const wrapper = await open('Clipping')
    await button(wrapper, '编辑 Worker').trigger('click')
    await wrapper.get('input[aria-label="并发切片任务数"]').setValue('5')
    await wrapper.get('input[aria-label="精确切片 CPU 编码线程数"]').setValue('4')
    for (const [language, title, spaceLabel, concurrencyLabel, threadsLabel] of [
      ['en-US', 'Clipping workers', 'Temporary space limit (GiB)', 'Concurrent clipping jobs', 'Exact clipping CPU encoding threads'],
      ['ja-JP', '切り抜き Worker', '一時領域の上限（GiB）', '同時切り抜きタスク数', '精密切り抜きのCPUエンコードスレッド数'],
    ] as const)
    {
      i18n.global.locale.value = language
      await flushPromises()
      expect(wrapper.text()).toContain(title)
      expect(wrapper.find(`input[aria-label="${spaceLabel}"]`).exists()).toBe(true)
      expect(wrapper.get(`input[aria-label="${concurrencyLabel}"]`).element).toHaveProperty('value', '5')
      expect(wrapper.get(`input[aria-label="${threadsLabel}"]`).element).toHaveProperty('value', '4')
    }
  })

  it('录像节点使用独立身份创建，并显示采集和待补传状态', async () =>
  {
    const transcodingWorker = { ...worker }
    const recordingWorker: RecordingWorker = {
      ...worker, id: 'recording-1', kind: 'Recording', name: '录像节点',
      configuration: defaultWorkerConfiguration('Recording'),
      telemetry: { ...worker.telemetry!, activeRecordings: 3, pendingUploadBytes: 1536, spoolBytes: 2 * 1024 ** 3 },
    }
    vi.mocked(TranscodingAPI.workers).mockResolvedValue([transcodingWorker, recordingWorker])
    vi.mocked(TranscodingAPI.createWorker).mockResolvedValueOnce({ worker: recordingWorker, secret: 'recording-key' })
    const wrapper = await open('Recording')
    expect(wrapper.text()).toContain('录像 Worker')
    expect(wrapper.text()).toContain('采集中 3 间')
    expect(wrapper.text()).toContain('待上传 1.5 KB')
    expect(wrapper.text()).toContain('录制缓存 2 GB / 20 GB')
    expect(wrapper.text()).not.toContain('本地 Worker')
    expect(wrapper.text()).not.toContain('h264_videotoolbox')
    await wrapper.get('button[aria-label="添加 Worker"]').trigger('click')
    await wrapper.get('input[aria-label="名称"]').setValue(' 录像节点 ')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(TranscodingAPI.createWorker).toHaveBeenCalledWith('录像节点', 'Recording')
    expect(wrapper.get('input[aria-label="访问密钥"]').element).toHaveProperty('value', 'recording-key')
  })

  it.each([false, true])('录像配置仅编辑采集并发和持久缓存，兼容旧完整配置为%s', async legacy =>
  {
    const recordingWorker: RecordingWorker = {
      ...worker, kind: 'Recording', configuration: { ...defaultWorkerConfiguration('Recording'), tempDirectory: '/srv/spool' },
    }
    if (legacy) { Object.assign(recordingWorker.configuration, worker.configuration, { tempDirectory: '/srv/spool', clipEncodingThreads: 4 }) }
    vi.mocked(TranscodingAPI.workers).mockResolvedValue([recordingWorker])
    const wrapper = await open('Recording')
    await button(wrapper, '编辑 Worker').trigger('click')
    const dialog = wrapper.get('[role="dialog"]')
    expect(dialog.find(`input[aria-label="${i18n.global.t('shell.settings.ffmpeg')}"]`).exists()).toBe(false)
    expect(dialog.find(`input[aria-label="${i18n.global.t('shell.settings.ffprobe')}"]`).exists()).toBe(false)
    expect(dialog.find('input[aria-label="下载上限（MiB/s）"]').exists()).toBe(false)
    expect(dialog.find('input[aria-label="首选编码器"]').exists()).toBe(false)
    expect(dialog.find('input[aria-label="GPU 并发任务数"]').exists()).toBe(false)
    expect(dialog.find('input[aria-label="CPU 并发任务数"]').exists()).toBe(false)
    expect(dialog.find('input[aria-label="精确切片 CPU 编码线程数"]').exists()).toBe(false)
    await dialog.get('input[aria-label="录制缓存目录"]').setValue('/srv/recording-cache')
    await dialog.get('input[aria-label="录制缓存上限（GiB）"]').setValue('4.5')
    await dialog.get('input[aria-label="同时录制房间数"]').setValue('8')
    await dialog.get('input[aria-label="上传上限（MiB/s）"]').setValue('1.5')
    await dialog.get('form').trigger('submit')
    await flushPromises()
    expect(TranscodingAPI.updateWorker).toHaveBeenCalledWith('node-1', expect.objectContaining({
      expectedRevision: 4,
      configuration: { tempDirectory: '/srv/recording-cache', maxConcurrentEncodes: 8, maxCacheBytes: 4831838208, minimumFreeBytes: 2 * 1024 ** 3, uploadBytesPerSecond: 1572864 },
    }))
    expect(wrapper.find('[role="dialog"]').exists()).toBe(false)
  })

  it('录像节点尚有归属时保留删除错误与确认状态', async () =>
  {
    const recordingWorker: RecordingWorker = { ...worker, kind: 'Recording', configuration: defaultWorkerConfiguration('Recording') }
    vi.mocked(TranscodingAPI.workers).mockResolvedValue([recordingWorker])
    const wrapper = await open('Recording')
    await wrapper.get('button[aria-label="删除 Worker"]').trigger('click')
    const dialog = wrapper.get('[role="dialog"]')
    expect(dialog.text()).toContain('仍有录制归属时，请先停用节点并等待补传完成')
    vi.mocked(TranscodingAPI.deleteWorker).mockRejectedValueOnce(new Error('节点仍有关联录制'))
    await button(dialog, '删除').trigger('click')
    await flushPromises()
    expect(dialog.text()).toContain('节点仍有关联录制')
    expect(wrapper.find('[role="dialog"]').exists()).toBe(true)
  })

  it('录像配置校验任务和空间，并在语言切换时保留失败草稿', async () =>
  {
    const recordingWorker: RecordingWorker = { ...worker, kind: 'Recording', configuration: defaultWorkerConfiguration('Recording') }
    vi.mocked(TranscodingAPI.workers).mockResolvedValue([recordingWorker])
    const wrapper = await open('Recording')
    await button(wrapper, '编辑 Worker').trigger('click')
    await wrapper.get('input[aria-label="同时录制房间数"]').setValue('0')
    await wrapper.get('form').trigger('submit')
    expect(TranscodingAPI.updateWorker).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('请填写名称，并检查录制并发、缓存空间和上传上限')
    for (const [language, title, spoolLabel, concurrencyLabel] of [
      ['en-US', 'Recording workers', 'Recording cache directory', 'Concurrent recording rooms'],
      ['ja-JP', '録画 Worker', '録画キャッシュディレクトリ', '同時録画ルーム数'],
    ] as const)
    {
      i18n.global.locale.value = language
      await flushPromises()
      expect(wrapper.text()).toContain(title)
      expect(wrapper.find(`input[aria-label="${spoolLabel}"]`).exists()).toBe(true)
      expect(wrapper.get(`input[aria-label="${concurrencyLabel}"]`).element).toHaveProperty('value', '0')
    }
  })
})
