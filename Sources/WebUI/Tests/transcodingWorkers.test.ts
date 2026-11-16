// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { defineComponent, h } from 'vue'
import TranscodingWorkersPanel from '../src/components/common/TranscodingWorkersPanel.vue'
import { TranscodingAPI } from '../src/apis/TranscodingAPI'
import { defaultWorkerConfiguration, type TranscodingWorker, type WorkerKind } from '../src/models/transcoding'
import { i18n } from '../src/plugins/i18n'

vi.mock('../src/apis/TranscodingAPI', () => ({ TranscodingAPI: { workers: vi.fn(), createWorker: vi.fn(), updateWorker: vi.fn(), rotateWorkerKey: vi.fn(), deleteWorker: vi.fn() } }))
const field = defineComponent({ props: ['modelValue', 'label', 'disabled', 'readonly'], emits: ['update:modelValue'], setup: (props, { emit }) => () => h('input', { value: props.modelValue, 'aria-label': props.label, disabled: props.disabled, readonly: props.readonly, onInput: (event: Event) => emit('update:modelValue', (event.target as HTMLInputElement).value) }) })
const stubs = {
  ContentCard: { props: ['title'], template: '<section><h2>{{ title }}</h2><slot name="header-actions" /><slot /><slot name="actions" /></section>' },
  VBtn: { props: ['disabled', 'loading'], template: '<button :disabled="disabled || loading"><slot /></button>' },
  VTooltip: { template: '<slot name="activator" :props="{}" />' },
  VTextField: field, VTextarea: field, VSelect: field,
  VSwitch: { props: ['modelValue', 'label', 'disabled'], emits: ['update:modelValue'], template: '<input type="checkbox" :aria-label="label" :checked="modelValue" :disabled="disabled" @change="$emit(\'update:modelValue\', $event.target.checked)" />' },
  VDialog: { props: ['modelValue'], template: '<div v-if="modelValue" role="dialog"><slot /></div>' },
  VAlert: { template: '<div role="alert"><slot /></div>' }, VSpacer: true, VIcon: true, StatusChip: true, VChip: { template: '<span><slot /></span>' },
}
const mounted: VueWrapper[] = []
let worker: TranscodingWorker

async function open(kind?: WorkerKind): Promise<VueWrapper>
{
  const wrapper = mount(TranscodingWorkersPanel, { props: { kind }, attachTo: document.body, global: { plugins: [i18n], stubs } })
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
  i18n.global.locale.value = 'zh-CN'
  worker = { id: 'node-1', name: '本地 Worker', enabled: true, configuration: defaultWorkerConfiguration(), configurationRevision: 4, appliedConfigurationRevision: 3, configurationError: null, capabilities: { operatingSystem: 'Darwin', architecture: 'Arm64', toolVersion: '7', encoders: [{ encoder: 'h264_videotoolbox', deviceId: null, available: true }] }, telemetry: { cpuPercent: null, cpuThreadBudget: 4, runningGpu: 1, runningCpu: 0, freeBytes: 1024 ** 3 }, lastSeenAtUtc: new Date().toISOString() }
  vi.mocked(TranscodingAPI.workers).mockResolvedValue([worker])
  vi.mocked(TranscodingAPI.createWorker).mockResolvedValue({ worker, secret: 'one-time-key' })
  vi.mocked(TranscodingAPI.updateWorker).mockResolvedValue(worker)
  vi.mocked(TranscodingAPI.rotateWorkerKey).mockResolvedValue({ secret: 'rotated-key' })
})
afterEach(() => { mounted.splice(0).forEach(wrapper => wrapper.unmount()); document.body.innerHTML = ''; vi.useRealTimers() })

describe('Worker管理', () =>
{
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
    const clippingWorker: TranscodingWorker = { ...worker, id: 'clip-1', name: '切片节点', kind: 'Clipping' }
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
    worker.kind = 'Clipping'
    worker.configuration.gpuConcurrency = 0
    worker.configuration.cpuConcurrency = 0
    const wrapper = await open('Clipping')
    await button(wrapper, '编辑 Worker').trigger('click')
    const dialog = wrapper.get('[role="dialog"]')
    expect(dialog.find('input[aria-label="首选编码器"]').exists()).toBe(false)
    expect(dialog.find('input[aria-label="GPU 并发任务数"]').exists()).toBe(false)
    expect(dialog.find('input[aria-label="CPU 负载平均窗口（秒）"]').exists()).toBe(false)
    expect(dialog.find('input[aria-label="允许 CPU 回退"]').exists()).toBe(false)
    await dialog.get('input[aria-label="临时空间上限（GiB）"]').setValue('4.5')
    await dialog.get('input[aria-label="总并发任务数"]').setValue('3')
    await dialog.get('form').trigger('submit')
    await flushPromises()
    expect(TranscodingAPI.updateWorker).toHaveBeenCalledWith('node-1', expect.objectContaining({
      expectedRevision: 4,
      configuration: expect.objectContaining({ maxConcurrentEncodes: 3, maxCacheBytes: 4831838208, gpuConcurrency: 0, cpuConcurrency: 0 }),
    }))
    expect(wrapper.find('[role="dialog"]').exists()).toBe(false)
  })

  it('切片配置在三语切换后更新标题和临时空间标签并保留草稿', async () =>
  {
    worker.kind = 'Clipping'
    const wrapper = await open('Clipping')
    await button(wrapper, '编辑 Worker').trigger('click')
    await wrapper.get('input[aria-label="总并发任务数"]').setValue('5')
    for (const [language, title, spaceLabel, concurrencyLabel] of [
      ['en-US', 'Clipping workers', 'Temporary space limit (GiB)', 'Total concurrent jobs'],
      ['ja-JP', '切り抜き Worker', '一時領域の上限（GiB）', '合計同時実行数'],
    ] as const)
    {
      i18n.global.locale.value = language
      await flushPromises()
      expect(wrapper.text()).toContain(title)
      expect(wrapper.find(`input[aria-label="${spaceLabel}"]`).exists()).toBe(true)
      expect(wrapper.get(`input[aria-label="${concurrencyLabel}"]`).element).toHaveProperty('value', '5')
    }
  })
})
