// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { nextTick } from 'vue'
import { createVuetify } from 'vuetify'
import { VTab, VTabs } from 'vuetify/components/VTabs'
import { VBtn } from 'vuetify/components/VBtn'
import { MediaAPI } from '../src/apis/MediaAPI'
import { TranscodingAPI } from '../src/apis/TranscodingAPI'
import { TaskHistoryAPI } from '../src/apis/TaskHistoryAPI'
import { defaultWorkerConfiguration, type ClippingWorker, type RecordingWorker, type TranscodingWorker, type Worker } from '../src/models/transcoding'
import { i18n } from '../src/plugins/i18n'
import WorkersView from '../src/views/WorkersView.vue'

vi.mock('../src/apis/MediaAPI', () => ({ MediaAPI: { settings: vi.fn(), updateSettings: vi.fn() } }))
vi.mock('../src/apis/TranscodingAPI', () => ({ TranscodingAPI: { workers: vi.fn() } }))
vi.mock('../src/apis/TaskHistoryAPI', () => ({ TaskHistoryAPI: { active: vi.fn(), detail: vi.fn() } }))

const container = { template: '<div><slot /></div>' }
const stubs = {
  ContentCard: { props: ['title'], template: '<section><h2>{{ title }}</h2><slot name="header-actions" /><slot /><slot name="actions" /></section>' },
  VTooltip: { template: '<slot name="activator" :props="{}" />' },
  VDialog: { props: ['modelValue'], template: '<div v-if="modelValue"><slot /></div>' },
  VAlert: container, VSpacer: true, VIcon: true, StatusChip: true,
  VChip: container, VTextField: true, VTextarea: true, VSwitch: true, VSelect: true,
  ConfirmDialog: true, TranscodingLogDialog: true,
}
const mounted: VueWrapper[] = []
let transcoding: TranscodingWorker
let clipping: ClippingWorker
let recording: RecordingWorker

async function open(): Promise<VueWrapper>
{
  const wrapper = mount(WorkersView, {
    attachTo: document.body,
    global: { plugins: [i18n, createVuetify({ components: { VTabs, VTab, VBtn } })], stubs },
  })
  mounted.push(wrapper)
  await flushPromises()
  return wrapper
}

async function selectTab(wrapper: VueWrapper, label: string): Promise<void>
{
  const tab = wrapper.findAll('[role="tab"]').find(tab => tab.text() === label)
  expect(tab).toBeDefined()
  await tab!.trigger('click')
  await flushPromises()
}

beforeEach(() =>
{
  vi.resetAllMocks()
  vi.useFakeTimers()
  vi.stubGlobal('matchMedia', vi.fn(() => ({ matches: false, addEventListener: vi.fn(), removeEventListener: vi.fn() })))
  vi.stubGlobal('ResizeObserver', vi.fn(function () { return { observe: vi.fn(), unobserve: vi.fn(), disconnect: vi.fn() } }))
  i18n.global.locale.value = 'zh-CN'
  transcoding = { id: 'transcoding-1', kind: 'Transcoding', name: '转码节点甲', enabled: true, configuration: defaultWorkerConfiguration(), configurationRevision: 1, appliedConfigurationRevision: 1, lastSeenAtUtc: null, configurationError: null }
  clipping = { ...transcoding, id: 'clipping-1', kind: 'Clipping', name: '切片节点乙', configuration: defaultWorkerConfiguration('Clipping') }
  recording = { ...transcoding, id: 'recording-1', kind: 'Recording', name: '录像节点丙', configuration: defaultWorkerConfiguration('Recording') }
  vi.mocked(TranscodingAPI.workers).mockResolvedValue([transcoding, clipping, recording])
  vi.mocked(TaskHistoryAPI.active).mockResolvedValue({})
})

afterEach(() =>
{
  mounted.splice(0).forEach(wrapper => wrapper.unmount())
  document.body.innerHTML = ''
  vi.useRealTimers()
  vi.unstubAllGlobals()
  i18n.global.locale.value = 'zh-CN'
})

describe('独立Worker管理页', () =>
{
  it('默认显示转码节点，独立加载Worker数据', async () =>
  {
    const wrapper = await open()
    expect(wrapper.get('h1').text()).toBe('Worker')
    expect(wrapper.findAll('[role="tab"]').map(tab => tab.text())).toEqual(['转码', '切片', '录像'])
    expect(wrapper.text()).toContain('转码节点甲')
    expect(wrapper.text()).not.toContain('切片节点乙')
    expect(TranscodingAPI.workers).toHaveBeenCalledTimes(1)
    expect(MediaAPI.settings).not.toHaveBeenCalled()
    expect(MediaAPI.updateSettings).not.toHaveBeenCalled()
  })

  it('切换类型卸载旧面板并仅保留当前类型的轮询', async () =>
  {
    const wrapper = await open()
    const firstRequest = vi.mocked(TranscodingAPI.workers).mock.calls[0]![0]!
    await selectTab(wrapper, '切片')
    expect(firstRequest.aborted).toBe(true)
    expect(wrapper.text()).toContain('切片节点乙')
    expect(wrapper.text()).not.toContain('转码节点甲')
    expect(TranscodingAPI.workers).toHaveBeenCalledTimes(2)
    await vi.advanceTimersByTimeAsync(5000)
    expect(TranscodingAPI.workers).toHaveBeenCalledTimes(3)
    await selectTab(wrapper, '转码')
    expect(wrapper.text()).toContain('转码节点甲')
    expect(wrapper.text()).not.toContain('切片节点乙')
    expect(TranscodingAPI.workers).toHaveBeenCalledTimes(4)
  })

  it('旧类型的迟到响应不能覆盖新类型列表', async () =>
  {
    let resolve!: (workers: Worker[]) => void
    vi.mocked(TranscodingAPI.workers).mockReturnValueOnce(new Promise(done => { resolve = done }))
    const wrapper = await open()
    await selectTab(wrapper, '切片')
    expect(wrapper.text()).toContain('切片节点乙')
    resolve([transcoding])
    await flushPromises()
    expect(wrapper.text()).toContain('切片节点乙')
    expect(wrapper.text()).not.toContain('转码节点甲')
    await vi.advanceTimersByTimeAsync(5000)
    expect(TranscodingAPI.workers).toHaveBeenCalledTimes(3)
  })

  it('录像分类仅显示录像节点并取消前一类型的在途请求', async () =>
  {
    const wrapper = await open()
    await selectTab(wrapper, '切片')
    const request = vi.mocked(TranscodingAPI.workers).mock.calls.at(-1)![0]!
    await selectTab(wrapper, '录像')
    expect(request.aborted).toBe(true)
    expect(wrapper.text()).toContain('录像节点丙')
    expect(wrapper.text()).not.toContain('切片节点乙')
    expect(wrapper.text()).not.toContain('转码节点甲')
    await vi.advanceTimersByTimeAsync(5000)
    expect(TranscodingAPI.workers).toHaveBeenCalledTimes(4)
  })

  it('页面标题和分类Tab随三语切换，节点名称保持原文', async () =>
  {
    const wrapper = await open()
    for (const [language, title, tabs] of [
      ['zh-CN', 'Worker', ['转码', '切片', '录像']],
      ['en-US', 'Workers', ['Transcoding', 'Clipping', 'Recording']],
      ['ja-JP', 'Worker', ['トランスコード', '切り抜き', '録画']],
    ] as const)
    {
      i18n.global.locale.value = language
      await nextTick()
      expect(wrapper.get('h1').text()).toBe(title)
      expect(wrapper.findAll('[role="tab"]').map(tab => tab.text())).toEqual(tabs)
      expect(wrapper.text()).toContain('转码节点甲')
    }
  })
})
