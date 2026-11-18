// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type DOMWrapper, type VueWrapper } from '@vue/test-utils'
import { createPinia, disposePinia, setActivePinia, type Pinia } from 'pinia'
import { defineComponent, h, ref } from 'vue'
import { createMemoryHistory, createRouter } from 'vue-router'
import SessionLiveMarkerPanel from '../src/components/player/SessionLiveMarkerPanel.vue'
import SessionPlaybackSidebar from '../src/components/player/SessionPlaybackSidebar.vue'
import SessionView from '../src/views/SessionView.vue'
import { useAuthStore } from '../src/stores/auth'
import { MediaAPI } from '../src/apis/MediaAPI'
import type { AiAnalysisJob, AiAnalysisStatus } from '../src/models/aiAnalysis'
import type { LiveMarkerFile } from '../src/models/liveMarkers'
import type { SessionDetail } from '../src/models/media'
import { i18n } from '../src/plugins/i18n'

const { liveMarkerStatus, generateLiveMarkers, cancel, files, detail, upload, remove } = vi.hoisted(() => ({ liveMarkerStatus: vi.fn(), generateLiveMarkers: vi.fn(), cancel: vi.fn(), files: vi.fn(), detail: vi.fn(), upload: vi.fn(), remove: vi.fn() }))
vi.mock('../src/apis/AiAnalysisAPI', () => ({ AiAnalysisAPI: { liveMarkerStatus, generateLiveMarkers, cancel } }))
vi.mock('../src/apis/LiveMarkerAPI', () => ({ LiveMarkerAPI: { files, detail, upload, remove } }))
const card = defineComponent({ props: ['title'], setup: (props, { slots }) => () => h('section', [props.title ? h('h2', props.title) : null, slots.default?.(), slots.actions?.(), slots.suffix?.()]) })
const button = defineComponent({ props: ['disabled', 'loading'], emits: ['click'], setup: (props, { slots, emit }) => () => h('button', { disabled: props.disabled || props.loading, onClick: () => emit('click') }, slots.default?.()) })
const menu = defineComponent({ setup: (_, { slots }) =>
{
  const opened = ref(false)
  return () => h('div', [slots.activator?.({ props: { onClick: () => { opened.value = !opened.value } } }), opened.value ? h('div', { role: 'menu', onClick: () => { opened.value = false } }, slots.default?.()) : null])
} })
const menuItem = defineComponent({ props: ['title', 'disabled'], emits: ['click'], setup: (props, { emit }) => () => h('button', { role: 'menuitem', disabled: props.disabled, onClick: () => emit('click') }, props.title) })
const select = defineComponent({ props: ['modelValue', 'items'], emits: ['update:modelValue'], setup: (props, { emit }) => () => h('select', { value: props.modelValue, onChange: (event: Event) => emit('update:modelValue', (event.target as HTMLSelectElement).value) }, props.items.map((item: { value: string; title: string }) => h('option', { value: item.value }, item.title))) })
const dialog = defineComponent({ props: ['modelValue'], setup: (props, { slots }) => () => props.modelValue ? h('div', { role: 'dialog' }, slots.default?.()) : null })
const stubs = {
  CardHeader: card, ContentCard: card, VCard: card, VCardText: card, VBtn: button, VMenu: menu, VList: card, VListItem: menuItem,
  VSelect: select, VDialog: dialog, VTabs: card, VTab: card, VAlert: card, VProgressLinear: true, VProgressCircular: true, VVirtualScroll: true, VIcon: true,
  VTooltip: { template: '<slot name="activator" :props="{}" />' }, SessionLiveMarkerUploadDialog: true, SessionLiveMarkerAlignmentDialog: true, ConfirmDialog: true,
  EventFeed: true, SessionTranslationPanel: true,
}
const mounted: VueWrapper[] = []
let pinia: Pinia
let archive: LiveMarkerFile[]
let status: AiAnalysisStatus

function file(id: string, time = '00:00:10'): LiveMarkerFile
{
  return { id, originalFileName: `${id}.yaml`, uploaderId: 'someone', uploaderName: '记录者', uploadedAtUtc: `2026-11-14T${time}Z`, entryCount: 1, calibrationMs: 1250, sourceTimeZoneId: 'Asia/Shanghai', canManage: false }
}
function job(id = 'generated', state: AiAnalysisJob['state'] = 'Running', fileId?: string): AiAnalysisJob
{
  return { id, sessionId: 'session-1', title: '录像', purpose: 'LiveMarkers', liveMarkerFileId: fileId, cancelRequested: false, state, phase: 'Analyzing', progress: 30, promptTokens: 50, completionTokens: 10, estimatedTokens: 0, tokenBudget: 1000000, modelRequests: 1, toolCalls: 2, errorCode: null, createdAtUtc: '', finishedAtUtc: null }
}
async function navigation(path = '/sessions/session-1')
{
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/sessions/:id', component: card }, { path: '/streamers/:id', component: card }, { path: '/analytics', component: card }] })
  await router.push(path)
  return router
}
async function mountPanel(props: { fileId?: string; editable?: boolean; ended?: boolean } = {})
{
  const wrapper = mount(SessionLiveMarkerPanel, { props: { sessionId: 'session-1', durationMs: 60000, active: true, ended: true, ...props }, global: { plugins: [pinia, i18n, await navigation()], stubs } })
  mounted.push(wrapper)
  await flushPromises()
  return wrapper
}
function findButton(wrapper: Pick<DOMWrapper<Element>, 'findAll'>, label: string)
{
  const value = wrapper.findAll('button').find(item => item.text() === label)
  if (!value) { throw new Error(`Missing button: ${label}`) }
  return value
}
async function openActions(wrapper: VueWrapper)
{
  await wrapper.get('button[aria-label="AI直播记录"]').trigger('click')
  return wrapper.get('[role="menu"]')
}
beforeEach(() =>
{
  vi.resetAllMocks()
  vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
  vi.stubGlobal('localStorage', { getItem: () => null, setItem: vi.fn(), removeItem: vi.fn() })
  pinia = createPinia()
  setActivePinia(pinia)
  useAuthStore().user = { id: 'viewer', email: 'viewer@example.test', displayName: '观众', role: 'User', status: 'Active', permissions: ['recordings.view', 'live-markers.generate'] }
  archive = [file('old'), file('recent', '00:00:20')]
  status = { enabled: true, canGenerate: true, currentJob: null, latestCompleted: null, latestPartial: null, stale: false }
  liveMarkerStatus.mockImplementation(async () => structuredClone(status))
  generateLiveMarkers.mockImplementation(async () => { status.currentJob = job('local', 'Queued'); return structuredClone(status.currentJob) })
  cancel.mockResolvedValue(undefined)
  files.mockImplementation(async () => ({ files: archive.map(value => ({ ...value })), defaultTimeZoneId: 'Asia/Shanghai' }))
  detail.mockImplementation(async (_sessionId: string, id: string) => ({ file: archive.find(value => value.id === id)!, startTime: '2026-11-14T00:00:00Z', entries: [] }))
  i18n.global.locale.value = 'zh-CN'
})
afterEach(() =>
{
  mounted.splice(0).forEach(wrapper => wrapper.unmount())
  disposePinia(pinia)
  vi.useRealTimers()
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
})

describe('AI 生成共享直播记录', () =>
{
  it('切换标签复用空闲的AI状态，已加载记录不继续轮询', async () =>
  {
    const wrapper = await mountPanel()
    expect(liveMarkerStatus).toHaveBeenCalledTimes(1)
    await wrapper.setProps({ active: false })
    await wrapper.setProps({ active: true })
    await flushPromises()
    await vi.advanceTimersByTimeAsync(60000)
    expect(liveMarkerStatus).toHaveBeenCalledTimes(1)
  })

  it('切出后保留本地生成任务，切回时检查完成并选中新记录', async () =>
  {
    const wrapper = await mountPanel({ fileId: 'old' })
    const actions = await openActions(wrapper)
    await findButton(actions, '生成直播记录').trigger('click')
    await flushPromises()
    await wrapper.setProps({ active: false })
    archive.push(file('ai-hidden', '00:00:30'))
    status.currentJob = job('local', 'Completed', 'ai-hidden')
    const count = liveMarkerStatus.mock.calls.length
    await vi.advanceTimersByTimeAsync(10000)
    expect(liveMarkerStatus).toHaveBeenCalledTimes(count)
    await wrapper.setProps({ active: true })
    await flushPromises()
    expect(wrapper.get<HTMLSelectElement>('select').element.value).toBe('ai-hidden')
    expect(generateLiveMarkers).toHaveBeenCalledTimes(1)
  })

  it('生成权限独立于分析和上传，页面进入与文件选择只读取', async () =>
  {
    const auth = useAuthStore()
    expect(auth.canGenerateLiveMarkers).toBe(true)
    expect(auth.canGenerateAnalytics).toBe(false)
    expect(auth.canViewAnalytics).toBe(false)
    expect(auth.canUploadLiveMarkers).toBe(false)
    const wrapper = await mountPanel()
    await wrapper.get('select').setValue('old')
    await flushPromises()
    expect(liveMarkerStatus).toHaveBeenCalledWith('session-1', expect.any(AbortSignal))
    expect(generateLiveMarkers).not.toHaveBeenCalled()
    expect(upload).not.toHaveBeenCalled()
    expect(wrapper.find('button[aria-label="上传记录"]').exists()).toBe(false)
    const actions = await openActions(wrapper)
    expect(findButton(actions, '生成直播记录').attributes('disabled')).toBeUndefined()
    expect(wrapper.text()).not.toContain('Token')
  })

  it('本页发起任务完成后选中新独立文件，保留旧文件及校准值', async () =>
  {
    const wrapper = await mountPanel({ fileId: 'old' })
    const original = archive.map(value => ({ ...value }))
    const actions = await openActions(wrapper)
    await findButton(actions, '生成直播记录').trigger('click')
    await flushPromises()
    expect(generateLiveMarkers).toHaveBeenCalledWith('session-1', false)
    expect(wrapper.get('[role="dialog"]').text()).toContain('直播记录生成详情')
    archive.push(file('ai-new', '00:00:30'))
    status.currentJob = job('local', 'Completed', 'ai-new')
    await vi.advanceTimersByTimeAsync(3000)
    await flushPromises()
    expect(wrapper.get<HTMLSelectElement>('select').element.value).toBe('ai-new')
    expect(wrapper.findAll('option')).toHaveLength(3)
    expect(archive.slice(0, 2)).toEqual(original)
    expect(remove).not.toHaveBeenCalled()
    expect(upload).not.toHaveBeenCalled()
    const link = wrapper.get('[role="dialog"] a')
    expect(link.attributes('href')).toBe('/sessions/session-1?panel=markers&liveMarkerFileId=ai-new')
    expect(link.attributes('target')).toBe('_blank')
  })

  it('他人的全局任务完成会刷新文件列表，不改变当前选择', async () =>
  {
    status.currentJob = job('other')
    const wrapper = await mountPanel({ fileId: 'old' })
    archive.push(file('shared-generated', '00:00:30'))
    status.currentJob = job('other', 'Completed', 'shared-generated')
    await vi.advanceTimersByTimeAsync(3000)
    await flushPromises()
    expect(wrapper.findAll('option')).toHaveLength(3)
    expect(wrapper.get<HTMLSelectElement>('select').element.value).toBe('old')
    expect(generateLiveMarkers).not.toHaveBeenCalled()
  })

  it('已删除的生成文件不会因查看或轮询被重建，显式重新生成使用 force', async () =>
  {
    status.currentJob = job('deleted', 'Completed')
    status.stale = true
    const wrapper = await mountPanel()
    await vi.advanceTimersByTimeAsync(30000)
    await flushPromises()
    expect(generateLiveMarkers).not.toHaveBeenCalled()
    expect(wrapper.findAll('option')).toHaveLength(2)
    const actions = await openActions(wrapper)
    await findButton(actions, '生成直播记录').trigger('click')
    await flushPromises()
    expect(generateLiveMarkers).toHaveBeenCalledWith('session-1', true)
  })

  it('链接指定的文件缺失时明确提示并保持未选中，用户仍可主动选择已有文件', async () =>
  {
    const wrapper = await mountPanel({ fileId: 'deleted-file' })
    expect(wrapper.text()).toContain('记录文件已删除或不存在')
    expect(wrapper.get<HTMLSelectElement>('select').element.value).toBe('')
    expect(wrapper.findAll('option')).toHaveLength(2)
    expect(detail).not.toHaveBeenCalled()
    await vi.advanceTimersByTimeAsync(30000)
    await flushPromises()
    expect(wrapper.get<HTMLSelectElement>('select').element.value).toBe('')
    expect(wrapper.text()).toContain('记录文件已删除或不存在')
    expect(detail).not.toHaveBeenCalled()
    await wrapper.get('select').setValue('old')
    await flushPromises()
    expect(detail).toHaveBeenCalledWith('session-1', 'old', expect.any(AbortSignal))
    expect(wrapper.text()).not.toContain('记录文件已删除或不存在')
    expect(generateLiveMarkers).not.toHaveBeenCalled()
  })

  it('发布阶段禁止取消，未结束和只读录像不允许生成', async () =>
  {
    status.currentJob = { ...job(), phase: 'Publishing', stages: [
      { id: 'Analyzing', state: 'Completed', progress: 100, elapsedMs: 2400, metrics: [] },
      { id: 'Reporting', state: 'Completed', progress: 100, elapsedMs: 1000, metrics: [] },
    ] }
    const wrapper = await mountPanel()
    const actions = await openActions(wrapper)
    expect(findButton(actions, '生成直播记录').attributes('disabled')).toBeDefined()
    await findButton(actions, '直播记录生成详情').trigger('click')
    const details = wrapper.get('[role="dialog"]')
    expect(details.text()).toContain('发布中')
    expect(details.get('[data-stage-id="Reporting"]').attributes('aria-pressed')).toBe('true')
    expect(details.text()).not.toContain('Token 用量')
    await details.get('[data-stage-id="Analyzing"]').trigger('click')
    expect(details.text()).toContain('Token 用量')
    const cancelButton = findButton(details, '取消生成')
    expect(cancelButton.attributes('disabled')).toBeDefined()
    await cancelButton.trigger('click')
    expect(cancel).not.toHaveBeenCalled()
    await findButton(wrapper.get('[role="dialog"]'), '关闭').trigger('click')
    status.currentJob = null
    await wrapper.setProps({ ended: false })
    await vi.advanceTimersByTimeAsync(3000)
    const unfinished = await openActions(wrapper)
    expect(findButton(unfinished, '生成直播记录').attributes('disabled')).toBeDefined()
    await wrapper.setProps({ editable: false })
    expect(wrapper.get('[role="menu"]').text()).not.toContain('生成直播记录')
    expect(generateLiveMarkers).not.toHaveBeenCalled()
  })

  it('播放页 query 自动打开记录面板并选定文件，后续 query 变化更新选择', async () =>
  {
    const session: SessionDetail = { session: { id: 'session-1', streamerId: 'streamer', roomId: 1, title: '录像', coverUrl: '', createdAtUtc: '', startedAtUtc: '2026-11-14T00:00:00Z', endedAtUtc: '2026-11-14T00:01:00Z', lastMediaAtUtc: '', state: 'Completed', hasDanmu: false, totalBytes: 0, isTrashed: false, isPermanent: false, mediaRevision: 1 }, segments: [], gaps: [], durationMs: 60000 }
    vi.spyOn(MediaAPI, 'session').mockResolvedValue(session)
    vi.spyOn(MediaAPI, 'streamers').mockResolvedValue([])
    vi.spyOn(MediaAPI, 'bookmarks').mockResolvedValue([])
    const router = await navigation('/sessions/session-1?liveMarkerFileId=old')
    const wrapper = mount(SessionView, { global: { plugins: [pinia, i18n, router], stubs: { ...stubs, MediaPlayer: true, ClipDialog: true, SessionDownloadDialog: true, PlaybackBookmarkActions: true, SessionBookmarkList: true, SessionBookmarkDialog: true, SessionGroupDialog: true, SessionSplitDialog: true, SessionConfigurationDialog: true, StatusChip: true, VSnackbar: true } } })
    mounted.push(wrapper)
    await flushPromises()
    expect(wrapper.findComponent(SessionPlaybackSidebar).props('liveMarkerFileId')).toBe('old')
    expect(wrapper.findComponent(SessionLiveMarkerPanel).props()).toMatchObject({ active: true, ended: true })
    expect(wrapper.get<HTMLSelectElement>('select').element.value).toBe('old')
    await router.push('/sessions/session-1?panel=markers&liveMarkerFileId=recent')
    await flushPromises()
    expect(wrapper.get<HTMLSelectElement>('select').element.value).toBe('recent')
    expect(generateLiveMarkers).not.toHaveBeenCalled()
  })

  it('撤销观看权限后旧响应不能恢复任务详情或记录文件', async () =>
  {
    const wrapper = await mountPanel()
    let resolve!: (value: AiAnalysisStatus) => void
    liveMarkerStatus.mockReturnValueOnce(new Promise<AiAnalysisStatus>(accept => { resolve = accept }))
    await wrapper.get('[aria-label="刷新记录"]').trigger('click')
    expect(liveMarkerStatus).toHaveBeenCalledTimes(2)
    const signal = liveMarkerStatus.mock.calls.at(-1)![1] as AbortSignal
    useAuthStore().user!.permissions = []
    await flushPromises()
    resolve({ ...status, currentJob: job('old', 'Completed', 'old-file') })
    await flushPromises()
    expect(signal.aborted).toBe(true)
    expect(wrapper.find('select').exists()).toBe(false)
    expect(wrapper.find('button[aria-label="AI直播记录"]').exists()).toBe(false)
    expect(wrapper.find('[role="dialog"]').exists()).toBe(false)
  })
})
