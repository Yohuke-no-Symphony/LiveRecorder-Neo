// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { nextTick } from 'vue'
import RecordingWorkerAssignmentsDialog from '../src/components/common/RecordingWorkerAssignmentsDialog.vue'
import TranscodingWorkersPanel from '../src/components/common/TranscodingWorkersPanel.vue'
import { TranscodingAPI } from '../src/apis/TranscodingAPI'
import { MediaAPI } from '../src/apis/MediaAPI'
import { defaultWorkerConfiguration, type RecordingWorkerAssignment, type WorkerKind } from '../src/models/transcoding'
import { i18n } from '../src/plugins/i18n'

vi.mock('../src/apis/TranscodingAPI', () => ({ TranscodingAPI: { workers: vi.fn(), workerAssignments: vi.fn() } }))
vi.mock('../src/apis/MediaAPI', () => ({ MediaAPI: { avatar: vi.fn(id => `/api/streamers/${id}/avatar`) } }))

const stubs = {
  VDialog: { props: ['modelValue'], emits: ['update:modelValue'], template: '<div v-if="modelValue" role="dialog"><slot /></div>' },
  ContentCard: { props: ['title'], template: '<section><h2>{{ title }}</h2><slot name="header-actions" /><slot /><slot name="actions" /></section>' },
  VBtn: { props: ['loading'], template: '<button :disabled="loading"><slot /></button>' },
  VAlert: { template: '<div role="alert"><slot /></div>' },
  VProgressLinear: { template: '<div role="progressbar" />' },
  UserAvatar: { props: ['name', 'avatarUrl'], template: '<img :src="avatarUrl" :alt="name" />' },
  VTooltip: { template: '<slot name="activator" :props="{}" />' },
  VSpacer: true, StatusChip: true,
}
const mounted: VueWrapper[] = []
const room: RecordingWorkerAssignment = { targetId: 'target-1', roomId: 123456, name: '测试主播' }

async function open(id = 'recording-1'): Promise<VueWrapper>
{
  const wrapper = mount(RecordingWorkerAssignmentsDialog, {
    props: { worker: { id, name: '录像节点甲' } },
    global: { plugins: [i18n], stubs },
  })
  mounted.push(wrapper)
  await flushPromises()
  return wrapper
}

beforeEach(() =>
{
  vi.clearAllMocks()
  i18n.global.locale.value = 'zh-CN'
  vi.mocked(TranscodingAPI.workerAssignments).mockReset().mockResolvedValue([room])
  vi.mocked(TranscodingAPI.workers).mockReset().mockResolvedValue([])
})

afterEach(() =>
{
  mounted.splice(0).forEach(wrapper => wrapper.unmount())
  i18n.global.locale.value = 'zh-CN'
})

describe('录像 Worker 分配情况', () =>
{
  it('仅录像节点显示分配情况按钮，点击后打开对应节点弹窗并可关闭', async () =>
  {
    const shared = { enabled: false, configurationRevision: 1, appliedConfigurationRevision: 1, lastSeenAtUtc: null, configurationError: null }
    const workers = [
      { ...shared, id: 'transcoding-1', kind: 'Transcoding' as const, name: '转码节点', configuration: defaultWorkerConfiguration() },
      { ...shared, id: 'clipping-1', kind: 'Clipping' as const, name: '切片节点', configuration: { ...defaultWorkerConfiguration(), clipEncodingThreads: 2 } },
      { ...shared, id: 'recording-1', kind: 'Recording' as const, name: '录像节点甲', configuration: defaultWorkerConfiguration() },
    ]
    vi.mocked(TranscodingAPI.workers).mockResolvedValue(workers)
    for (const kind of ['Transcoding', 'Clipping', 'Recording'] satisfies WorkerKind[])
    {
      const wrapper = mount(TranscodingWorkersPanel, { props: { kind }, global: { plugins: [i18n], stubs } })
      mounted.push(wrapper)
      await flushPromises()
      const button = wrapper.findAll('button').find(button => button.text() === '分配情况')
      expect(!!button).toBe(kind === 'Recording')
      if (button)
      {
        await button.trigger('click')
        await flushPromises()
        expect(wrapper.get('[role="dialog"]').text()).toContain('测试主播')
        expect(TranscodingAPI.workerAssignments).toHaveBeenCalledWith('recording-1', expect.any(AbortSignal))
        const signal = vi.mocked(TranscodingAPI.workerAssignments).mock.calls.at(-1)![1]!
        await wrapper.findAll('button').find(button => button.text() === '关闭')!.trigger('click')
        expect(wrapper.find('[role="dialog"]').exists()).toBe(false)
        expect(signal.aborted).toBe(true)
      }
    }
  })

  it('按节点读取房间，按头像、主播名称和房间号显示', async () =>
  {
    const wrapper = await open()
    expect(TranscodingAPI.workerAssignments).toHaveBeenCalledWith('recording-1', expect.any(AbortSignal))
    expect(wrapper.get('h2').text()).toBe('分配情况 · 录像节点甲')
    expect(wrapper.get('li').text()).toContain('测试主播')
    expect(wrapper.get('li').text()).toContain('123456')
    expect(wrapper.get('li img').attributes('src')).toBe('/api/streamers/target-1/avatar')
    expect(MediaAPI.avatar).toHaveBeenCalledWith('target-1')
  })

  it('读取中不显示空态，完成后可以显示无分配房间', async () =>
  {
    let resolve!: (rooms: RecordingWorkerAssignment[]) => void
    vi.mocked(TranscodingAPI.workerAssignments).mockReturnValueOnce(new Promise(done => { resolve = done }))
    const wrapper = await open()
    expect(wrapper.find('[role="progressbar"]').exists()).toBe(true)
    expect(wrapper.text()).not.toContain('暂无分配的直播间')
    resolve([])
    await flushPromises()
    expect(wrapper.find('[role="progressbar"]').exists()).toBe(false)
    expect(wrapper.text()).toContain('暂无分配的直播间')
  })

  it('失败后显示错误，刷新可以重新读取', async () =>
  {
    vi.mocked(TranscodingAPI.workerAssignments).mockRejectedValueOnce(new Error('连接服务器失败'))
    const wrapper = await open()
    expect(wrapper.get('[role="alert"]').text()).toBe('连接服务器失败')
    expect(wrapper.text()).not.toContain('暂无分配的直播间')
    await wrapper.get('button[aria-label="刷新"]').trigger('click')
    await flushPromises()
    expect(wrapper.find('[role="alert"]').exists()).toBe(false)
    expect(wrapper.get('li').text()).toContain('测试主播')
  })

  it('切换节点会取消旧请求，迟到响应不能替换新节点房间', async () =>
  {
    let resolve!: (rooms: RecordingWorkerAssignment[]) => void
    vi.mocked(TranscodingAPI.workerAssignments).mockReturnValueOnce(new Promise(done => { resolve = done }))
    const wrapper = await open()
    const signal = vi.mocked(TranscodingAPI.workerAssignments).mock.calls[0]![1]!
    await wrapper.setProps({ worker: { id: 'recording-2', name: '录像节点乙' } })
    await flushPromises()
    expect(signal.aborted).toBe(true)
    expect(wrapper.get('h2').text()).toContain('录像节点乙')
    resolve([{ ...room, roomId: 999, name: '旧节点房间' }])
    await flushPromises()
    expect(wrapper.text()).not.toContain('旧节点房间')
    expect(wrapper.get('li').text()).toContain('测试主播')
  })

  it('关闭和卸载时取消请求', async () =>
  {
    vi.mocked(TranscodingAPI.workerAssignments).mockReturnValueOnce(new Promise(() => {}))
    const wrapper = await open()
    const signal = vi.mocked(TranscodingAPI.workerAssignments).mock.calls[0]![1]!
    const close = wrapper.findAll('button').find(button => button.text() === '关闭')!
    await close.trigger('click')
    expect(wrapper.emitted('close')).toHaveLength(1)
    wrapper.unmount()
    mounted.splice(mounted.indexOf(wrapper), 1)
    expect(signal.aborted).toBe(true)
  })

  it('切换三语只改变界面文案，主播名称和房间号保持原文', async () =>
  {
    const wrapper = await open()
    for (const [language, title, close] of [
      ['en-US', 'Assignments · 录像节点甲', 'Close'],
      ['ja-JP', '割り当て状況 · 录像节点甲', '閉じる'],
    ] as const)
    {
      i18n.global.locale.value = language
      await nextTick()
      expect(wrapper.get('h2').text()).toBe(title)
      expect(wrapper.text()).toContain(close)
      expect(wrapper.get('li').text()).toContain('测试主播')
      expect(wrapper.get('li').text()).toContain('123456')
    }
  })
})
