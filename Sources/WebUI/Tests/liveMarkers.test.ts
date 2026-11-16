// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { defineComponent, h, nextTick, ref } from 'vue'
import { ApiError, setCsrfToken } from '../src/apis/APIBase'
import { LiveMarkerAPI } from '../src/apis/LiveMarkerAPI'
import { useSessionLiveMarkers } from '../src/composables/useSessionLiveMarkers'
import type { LiveMarkerDetail, LiveMarkerEntry, LiveMarkerFile, LiveMarkerFileList } from '../src/models/liveMarkers'
import { i18n } from '../src/plugins/i18n'
import SessionLiveMarkerPanel from '../src/components/player/SessionLiveMarkerPanel.vue'
import SessionLiveMarkerUploadDialog from '../src/components/player/SessionLiveMarkerUploadDialog.vue'
import SessionLiveMarkerAlignmentDialog from '../src/components/player/SessionLiveMarkerAlignmentDialog.vue'
import { canSeekLiveMarker, formatLiveMarkerOffset, parseLiveMarkerCalibration } from '../src/utilities/liveMarkers'
import { previewCacheKey } from '../src/utilities/previewBrowserCache'

const previewCache = vi.hoisted(() => ({ values: new Map<string, unknown>(), read: vi.fn(), write: vi.fn(), remove: vi.fn() }))
vi.mock('../src/utilities/previewBrowserCache', () => ({
  previewCacheKey: (kind: string, id: string, scope: string, revision: string) => JSON.stringify([kind, id, scope, revision]),
  readPreviewCache: previewCache.read,
  writePreviewCache: previewCache.write,
  removePreviewCache: previewCache.remove,
}))

const auth = vi.hoisted(() => ({ canUploadLiveMarkers: true, canViewRecordings: true, canGenerateLiveMarkers: false }))
vi.mock('../src/stores/auth', () => ({ useAuthStore: () => auth }))
vi.mock('../src/apis/AiAnalysisAPI', () => ({ AiAnalysisAPI: { liveMarkerStatus: vi.fn().mockResolvedValue({ enabled: false, canGenerate: false, currentJob: null, latestCompleted: null, latestPartial: null, stale: false }) } }))

const mounted: VueWrapper[] = []
const scrollToIndex = vi.fn()
const container = defineComponent({ setup: (_, { slots }) => () => h('div', slots.default?.()) })
const dialog = defineComponent({
  props: ['modelValue', 'persistent', 'scrollable'],
  setup(props, { slots }) { return () => props.modelValue ? h('section', slots.default?.()) : null },
})
const button = defineComponent({
  props: ['disabled', 'loading', 'type', 'form', 'icon'],
  setup(props, { slots })
  {
    return () => h('button', { type: props.type ?? 'button', form: props.form, disabled: props.disabled || props.loading }, slots.default?.())
  },
})
const textField = defineComponent({
  props: ['modelValue', 'label', 'type', 'step', 'disabled', 'required', 'hint'], emits: ['update:modelValue'],
  setup(props, { emit })
  {
    return () => h('label', [props.label, h('input', {
      value: props.modelValue, type: props.type ?? 'text', step: props.step, disabled: props.disabled, required: props.required,
      onInput: (event: Event) => emit('update:modelValue', (event.target as HTMLInputElement).value),
    }), h('span', props.hint)])
  },
})
const fileInput = defineComponent({
  props: ['modelValue', 'label', 'disabled', 'required', 'accept', 'hint'], emits: ['update:modelValue'],
  setup(props, { emit })
  {
    return () => h('label', [props.label, h('input', {
      type: 'file', disabled: props.disabled, required: props.required, accept: props.accept,
      onChange: (event: Event) => emit('update:modelValue', (event.target as HTMLInputElement).files?.[0] ?? null),
    }), h('span', props.hint)])
  },
})
const select = defineComponent({
  props: ['modelValue', 'label', 'items', 'disabled'], emits: ['update:modelValue'],
  setup(props, { emit })
  {
    return () => h('label', [props.label, h('select', {
      value: props.modelValue, disabled: props.disabled,
      onChange: (event: Event) => emit('update:modelValue', (event.target as HTMLSelectElement).value),
    }, (props.items as { title: string; value: string }[]).map(item => h('option', { value: item.value }, item.title)))])
  },
})
const virtualScroll = defineComponent({
  props: ['items'],
  setup(props, { slots, expose })
  {
    expose({ scrollToIndex })
    return () => h('div', (props.items as LiveMarkerEntry[]).flatMap(item => slots.default?.({ item }) ?? []))
  },
})
const stubs = {
  VCard: container, VCardText: container, VDialog: dialog, VBtn: button, VAlert: container,
  VFileInput: fileInput, VTextField: textField, VSelect: select, VVirtualScroll: virtualScroll,
  VIcon: true, VListItem: container, VProgressLinear: true, VTooltip: { props: ['text', 'location', 'disabled'], template: '<slot name="activator" :props="{}" />' },
  AiLiveMarkerActions: true, AiAnalysisDetailsDialog: true,
}

function file(id: string, values: Partial<LiveMarkerFile> = {}): LiveMarkerFile
{
  return {
    id, originalFileName: `${id}.yaml`, uploaderId: 'user-1', uploaderName: '记录者',
    uploadedAtUtc: '2026-11-13T00:00:10Z', entryCount: 2, calibrationMs: 0,
    sourceTimeZoneId: 'Asia/Shanghai', canManage: true, ...values,
  }
}

function entry(index: number, offsetMs: number, values: Partial<LiveMarkerEntry> = {}): LiveMarkerEntry
{
  return { index, realDateTime: '2026-11-13T00:00:01Z', liveTime: '00:00:01', message: `打点${index}`, markerColor: 'Magenta', offsetMs, ...values }
}

function detail(value: LiveMarkerFile, entries = [entry(0, 1000), entry(1, 2000)]): LiveMarkerDetail
{
  return { file: { ...value }, startTime: '2026-11-13T00:00:00Z', entries }
}

function setupFeed(activeInitially = true)
{
  const id = ref('session-1')
  const active = ref(activeInitially)
  const scope = ref('viewer-1')
  const preferredFileId = ref<string>()
  let feed!: ReturnType<typeof useSessionLiveMarkers>
  const wrapper = mount(defineComponent({
    setup()
    {
      feed = useSessionLiveMarkers(id, active, preferredFileId, scope)
      return () => h('div')
    },
  }), { global: { plugins: [i18n] } })
  mounted.push(wrapper)
  return { id, active, scope, preferredFileId, wrapper, get feed() { return feed } }
}

function deferred<T>()
{
  let resolve!: (value: T) => void
  const promise = new Promise<T>(accept => { resolve = accept })
  return { promise, resolve }
}

function mockArchive(initialFiles = [file('older'), file('newer', { uploadedAtUtc: '2026-11-13T00:00:20Z' })])
{
  const archive = { files: initialFiles }
  const files = vi.spyOn(LiveMarkerAPI, 'files').mockImplementation(async () => ({ files: archive.files.map(value => ({ ...value })), defaultTimeZoneId: 'Asia/Shanghai' }))
  const read = vi.spyOn(LiveMarkerAPI, 'detail').mockImplementation(async (_sessionId, fileId) =>
  {
    const value = archive.files.find(item => item.id === fileId)
    if (!value) { throw new ApiError(404, '记录文件不存在') }
    return detail(value, [entry(1, 2000 + value.calibrationMs), entry(0, 1000 + value.calibrationMs)])
  })
  return { archive, files, read }
}

beforeEach(() =>
{
  previewCache.values.clear()
  previewCache.read.mockReset().mockImplementation(async (key: string) => previewCache.values.get(key))
  previewCache.write.mockReset().mockImplementation(async (key: string, value: unknown) => { previewCache.values.set(key, structuredClone(value)) })
  previewCache.remove.mockReset().mockImplementation(async (key: string) => { previewCache.values.delete(key) })
  auth.canUploadLiveMarkers = true
  scrollToIndex.mockReset()
  i18n.global.locale.value = 'zh-CN'
  vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
})

afterEach(() =>
{
  mounted.splice(0).forEach(wrapper => wrapper.unmount())
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
  vi.unstubAllEnvs()
  vi.useRealTimers()
  setCsrfToken('')
  i18n.global.locale.value = 'zh-CN'
})

describe('直播记录 API', () =>
{
  it('上传原始文件与时间参数，沿用 Cookie、CSRF 和浏览器 multipart 边界', async () =>
  {
    const fetcher = vi.fn().mockResolvedValue(new Response(JSON.stringify(file('saved')), { headers: { 'Content-Type': 'application/json' } }))
    vi.stubGlobal('fetch', fetcher)
    vi.stubEnv('DEV', true)
    vi.stubEnv('VITE_API_BASE_URL', 'https://recordings.example.test')
    setCsrfToken('csrf-live-markers')
    const source = new File(['StartTime: 2026-11-13T08:00:00+08:00\nMarker: []\n'], '高光记录.yaml', { type: 'application/yaml' })

    expect(await LiveMarkerAPI.upload('session /中文', { file: source, timeZoneId: 'Asia/Shanghai', calibrationMs: -1250 })).toMatchObject({ id: 'saved' })
    const [url, options] = fetcher.mock.calls[0]!
    expect(url).toBe('https://recordings.example.test/api/sessions/session%20%2F%E4%B8%AD%E6%96%87/live-markers')
    expect(options).toMatchObject({ method: 'POST', credentials: 'include' })
    expect(options.body).toBeInstanceOf(FormData)
    const body = options.body as FormData
    expect(body.get('file')).toMatchObject({ name: '高光记录.yaml', size: source.size })
    expect(body.get('timeZoneId')).toBe('Asia/Shanghai')
    expect(body.get('calibrationMs')).toBe('-1250')
    expect((options.headers as Headers).get('X-CSRF-TOKEN')).toBe('csrf-live-markers')
    expect((options.headers as Headers).has('Content-Type')).toBe(false)
  })

  it('列表和详情分别编码场次/文件 ID，并传递取消信号', async () =>
  {
    const fetcher = vi.fn().mockResolvedValue(new Response(JSON.stringify({}), { headers: { 'Content-Type': 'application/json' } }))
    vi.stubGlobal('fetch', fetcher)
    vi.stubEnv('DEV', true)
    vi.stubEnv('VITE_API_BASE_URL', 'https://recordings.example.test')
    const controller = new AbortController()
    await LiveMarkerAPI.files('session ?#', controller.signal)
    fetcher.mockResolvedValueOnce(new Response(JSON.stringify({}), { headers: { 'Content-Type': 'application/json' } }))
    await LiveMarkerAPI.detail('session ?#', 'file /中文', controller.signal)
    expect(fetcher.mock.calls.map(([url]) => new URL(url).pathname)).toEqual([
      '/api/sessions/session%20%3F%23/live-markers',
      '/api/sessions/session%20%3F%23/live-markers/file%20%2F%E4%B8%AD%E6%96%87',
    ])
    expect(fetcher.mock.calls.every(([, options]) => options.signal === controller.signal)).toBe(true)
  })

  it('校准发送带符号毫秒，删除使用独立文件地址', async () =>
  {
    const fetcher = vi.fn().mockResolvedValue(new Response(null, { status: 204 }))
    vi.stubGlobal('fetch', fetcher)
    vi.stubEnv('DEV', true)
    vi.stubEnv('VITE_API_BASE_URL', 'https://recordings.example.test')
    await LiveMarkerAPI.align('s1', 'f1', -1500)
    await LiveMarkerAPI.remove('s1', 'f1')
    expect(fetcher.mock.calls[0]?.[0]).toBe('https://recordings.example.test/api/sessions/s1/live-markers/f1/alignment')
    expect(fetcher.mock.calls[0]?.[1]).toMatchObject({ method: 'PUT', body: JSON.stringify({ calibrationMs: -1500 }) })
    expect(fetcher.mock.calls[1]?.[0]).toBe('https://recordings.example.test/api/sessions/s1/live-markers/f1')
    expect(fetcher.mock.calls[1]?.[1].method).toBe('DELETE')
  })
})

describe('场次共享直播记录状态', () =>
{
  it('服务器确认文件版本后复用浏览器详情，重新进入不重复下载条目', async () =>
  {
    const current = file('current', { revision: 'revision-1' })
    const state = mockArchive([current])
    const first = setupFeed()
    await flushPromises()
    expect(state.read).toHaveBeenCalledTimes(1)
    first.wrapper.unmount()
    state.archive.files = [{ ...current, canManage: false }]
    const second = setupFeed()
    await flushPromises()
    expect(state.files).toHaveBeenCalledTimes(2)
    expect(state.read).toHaveBeenCalledTimes(1)
    expect(second.feed.detail.value?.entries.map(value => value.index)).toEqual([0, 1])
    expect(second.feed.detail.value?.file.canManage).toBe(false)
  })

  it('内容或场次投影版本变化不复用旧缓存，显式刷新强制读取服务器', async () =>
  {
    const state = mockArchive([file('current', { revision: 'revision-1' })])
    const first = setupFeed()
    await flushPromises()
    first.wrapper.unmount()
    state.archive.files = [file('current', { revision: 'revision-2', calibrationMs: -500 })]
    const second = setupFeed()
    await flushPromises()
    expect(state.read).toHaveBeenCalledTimes(2)
    expect(second.feed.detail.value?.entries.map(value => value.offsetMs)).toEqual([500, 1500])
    await second.feed.refresh()
    expect(state.read).toHaveBeenCalledTimes(3)
  })

  it('浏览器缓存身份隔离，身份切换期间迟到的缓存不能恢复旧详情', async () =>
  {
    const current = file('current', { revision: 'revision-1' })
    mockArchive([current])
    const pending = deferred<LiveMarkerDetail | undefined>()
    previewCache.read.mockReturnValueOnce(pending.promise)
    const mountedFeed = setupFeed()
    await flushPromises()
    mountedFeed.scope.value = 'viewer-2'
    await flushPromises()
    pending.resolve(detail(current, [entry(0, 9000, { message: '旧身份条目' })]))
    await flushPromises()
    expect(previewCache.read.mock.calls.map(([key]) => key)).toEqual([
      previewCacheKey('live-markers', 'session-1', 'viewer-1', JSON.stringify([current.id, current.revision])),
      previewCacheKey('live-markers', 'session-1', 'viewer-2', JSON.stringify([current.id, current.revision])),
    ])
    expect(mountedFeed.feed.detail.value?.file.id).toBe('current')
    expect(mountedFeed.feed.detail.value?.entries.map(value => value.message)).not.toContain('旧身份条目')
  })

  it('上传时间的小数秒精度不影响按真实时间选择最新文件', async () =>
  {
    mockArchive([
      file('older', { uploadedAtUtc: '2026-11-13T00:00:10Z' }),
      file('newer', { uploadedAtUtc: '2026-11-13T00:00:10.1000000Z' }),
    ])
    const mountedFeed = setupFeed()
    await flushPromises()
    expect(mountedFeed.feed.selectedId.value).toBe('newer')
  })

  it('初次选最新文件，刷新保留用户选择，失效选择回退到最新文件', async () =>
  {
    const state = mockArchive()
    const mountedFeed = setupFeed()
    await flushPromises()
    expect(mountedFeed.feed.selectedId.value).toBe('newer')
    expect(mountedFeed.feed.defaultTimeZoneId.value).toBe('Asia/Shanghai')
    expect(mountedFeed.feed.detail.value?.entries.map(value => value.index)).toEqual([0, 1])
    await mountedFeed.feed.select('older')
    state.archive.files.push(file('latest', { uploadedAtUtc: '2026-11-13T00:00:30Z' }))
    await mountedFeed.feed.refresh(false)
    expect(mountedFeed.feed.selectedId.value).toBe('older')
    state.archive.files = state.archive.files.filter(value => value.id !== 'older')
    await mountedFeed.feed.refresh(false)
    expect(mountedFeed.feed.selectedId.value).toBe('latest')
    expect(mountedFeed.feed.detail.value?.file.id).toBe('latest')
  })

  it('快速切换文件取消旧请求，迟到详情不覆盖当前选择', async () =>
  {
    const state = mockArchive()
    let completeOld!: (value: LiveMarkerDetail) => void
    state.read.mockImplementationOnce(() => new Promise(resolve => { completeOld = resolve }))
    const mountedFeed = setupFeed()
    await flushPromises()
    const oldSignal = state.read.mock.calls[0]?.[2] as AbortSignal
    await mountedFeed.feed.select('older')
    expect(oldSignal.aborted).toBe(true)
    expect(mountedFeed.feed.detail.value?.file.id).toBe('older')
    completeOld(detail(file('newer')))
    await flushPromises()
    expect(mountedFeed.feed.selectedId.value).toBe('older')
    expect(mountedFeed.feed.detail.value?.file.id).toBe('older')
  })

  it('场次切换取消旧列表，不接受旧场次的迟到结果', async () =>
  {
    const state = mockArchive([file('new-session')])
    let completeOld!: (value: LiveMarkerFileList) => void
    state.files.mockImplementationOnce(() => new Promise(resolve => { completeOld = resolve }))
    const mountedFeed = setupFeed()
    await flushPromises()
    const oldSignal = state.files.mock.calls[0]?.[1] as AbortSignal
    mountedFeed.id.value = 'session-2'
    await flushPromises()
    expect(oldSignal.aborted).toBe(true)
    expect(mountedFeed.feed.selectedId.value).toBe('new-session')
    completeOld({ files: [file('old-session')], defaultTimeZoneId: 'UTC' })
    await flushPromises()
    expect(mountedFeed.feed.selectedId.value).toBe('new-session')
    expect(mountedFeed.feed.defaultTimeZoneId.value).toBe('Asia/Shanghai')
  })

  it('首次激活读取，切回复用所选文件且活动期间不轮询', async () =>
  {
    const state = mockArchive()
    const mountedFeed = setupFeed(false)
    await flushPromises()
    await vi.advanceTimersByTimeAsync(20000)
    expect(state.files).not.toHaveBeenCalled()
    mountedFeed.active.value = true
    await flushPromises()
    await mountedFeed.feed.select('older')
    mountedFeed.active.value = false
    await flushPromises()
    const count = state.files.mock.calls.length
    await vi.advanceTimersByTimeAsync(20000)
    expect(state.files).toHaveBeenCalledTimes(count)
    expect(mountedFeed.feed.selectedId.value).toBe('older')
    mountedFeed.active.value = true
    await flushPromises()
    expect(state.files).toHaveBeenCalledTimes(count)
    expect(state.read).toHaveBeenCalledTimes(2)
    expect(mountedFeed.feed.detail.value?.file.id).toBe('older')
    await vi.advanceTimersByTimeAsync(30000)
    expect(state.files).toHaveBeenCalledTimes(count)
    expect(state.read).toHaveBeenCalledTimes(2)
  })

  it('空文件清单也是成功快照，重复激活不重新读取', async () =>
  {
    const state = mockArchive([])
    const mountedFeed = setupFeed()
    await flushPromises()
    for (let index = 0; index < 3; index++)
    {
      mountedFeed.active.value = false
      await nextTick()
      mountedFeed.active.value = true
      await flushPromises()
    }
    expect(state.files).toHaveBeenCalledTimes(1)
    expect(state.read).not.toHaveBeenCalled()
    expect(mountedFeed.feed.defaultTimeZoneId.value).toBe('Asia/Shanghai')
  })

  it('隐藏期间完成已启动清单及其详情，重复激活不产生并行请求', async () =>
  {
    const state = mockArchive([file('current')])
    const pending = deferred<LiveMarkerFileList>()
    state.files.mockReturnValueOnce(pending.promise)
    const mountedFeed = setupFeed()
    mountedFeed.active.value = false
    await nextTick()
    expect(state.files.mock.calls[0]![1]?.aborted).toBe(false)
    mountedFeed.active.value = true
    await nextTick()
    expect(state.files).toHaveBeenCalledTimes(1)
    mountedFeed.active.value = false
    await nextTick()
    pending.resolve({ files: state.archive.files, defaultTimeZoneId: 'Asia/Shanghai' })
    await flushPromises()
    expect(mountedFeed.feed.detail.value?.file.id).toBe('current')
    mountedFeed.active.value = true
    await flushPromises()
    expect(state.files).toHaveBeenCalledTimes(1)
    expect(state.read).toHaveBeenCalledTimes(1)
  })

  it('隐藏期间允许同一场次详情完成并缓存，选择当前文件不重读', async () =>
  {
    const state = mockArchive([file('current')])
    const pending = deferred<LiveMarkerDetail>()
    state.read.mockReturnValueOnce(pending.promise)
    const mountedFeed = setupFeed()
    await flushPromises()
    mountedFeed.active.value = false
    await nextTick()
    expect(state.read.mock.calls[0]![2]?.aborted).toBe(false)
    pending.resolve(detail(file('current')))
    await flushPromises()
    mountedFeed.active.value = true
    await flushPromises()
    await mountedFeed.feed.select('current')
    expect(state.files).toHaveBeenCalledTimes(1)
    expect(state.read).toHaveBeenCalledTimes(1)
  })

  it('详情失败后切回仅补读详情，成功时清除旧错误', async () =>
  {
    const state = mockArchive([file('current')])
    state.read.mockRejectedValueOnce(new Error('详情暂时不可读'))
    const mountedFeed = setupFeed()
    await flushPromises()
    expect(mountedFeed.feed.error.value).toBe('详情暂时不可读')
    mountedFeed.active.value = false
    await nextTick()
    mountedFeed.active.value = true
    await flushPromises()
    expect(state.files).toHaveBeenCalledTimes(1)
    expect(state.read).toHaveBeenCalledTimes(2)
    expect(mountedFeed.feed.detail.value?.file.id).toBe('current')
    expect(mountedFeed.feed.error.value).toBe('')
  })

  it('隐藏期间显式刷新会在恢复时替换在途旧清单，旧结果不能覆盖', async () =>
  {
    const state = mockArchive([file('current')])
    const pending = deferred<LiveMarkerFileList>()
    state.files.mockReturnValueOnce(pending.promise)
    const mountedFeed = setupFeed()
    mountedFeed.active.value = false
    await nextTick()
    await mountedFeed.feed.reveal('generated')
    state.archive.files.push(file('generated'))
    mountedFeed.active.value = true
    await flushPromises()
    expect(state.files).toHaveBeenCalledTimes(2)
    expect(state.files.mock.calls[0]![1]?.aborted).toBe(true)
    pending.resolve({ files: [file('old')], defaultTimeZoneId: 'UTC' })
    await flushPromises()
    expect(mountedFeed.feed.detail.value?.file.id).toBe('generated')
    expect(mountedFeed.feed.defaultTimeZoneId.value).toBe('Asia/Shanghai')
  })

  it('隐藏期间显式刷新或定位新文件会留待激活，不丢失更新意图', async () =>
  {
    const state = mockArchive([file('current')])
    const mountedFeed = setupFeed()
    await flushPromises()
    mountedFeed.active.value = false
    await nextTick()
    state.archive.files.push(file('generated', { uploadedAtUtc: '2026-11-13T00:00:30Z' }))
    await mountedFeed.feed.refresh(false)
    await mountedFeed.feed.reveal('generated')
    expect(state.files).toHaveBeenCalledTimes(1)
    mountedFeed.active.value = true
    await flushPromises()
    expect(state.files).toHaveBeenCalledTimes(2)
    expect(state.read).toHaveBeenCalledTimes(2)
    expect(mountedFeed.feed.detail.value?.file.id).toBe('generated')
  })

  it.each(['upload', 'align', 'remove'] as const)('%s 在隐藏期间完成，切回后补齐受影响详情', async mutation =>
  {
    const state = mockArchive()
    const mountedFeed = setupFeed()
    await flushPromises()
    const pending = deferred<void>()
    const saved = file(mutation === 'upload' ? 'uploaded' : 'newer', { calibrationMs: -500, uploadedAtUtc: '2026-11-13T00:00:30Z' })
    let operation: Promise<unknown>
    if (mutation === 'remove')
    {
      vi.spyOn(LiveMarkerAPI, 'remove').mockImplementationOnce(async () =>
      {
        await pending.promise
        state.archive.files = state.archive.files.filter(value => value.id !== 'newer')
      })
      operation = mountedFeed.feed.remove('newer')
    }
    else if (mutation === 'align')
    {
      vi.spyOn(LiveMarkerAPI, 'align').mockImplementationOnce(async () =>
      {
        await pending.promise
        state.archive.files = state.archive.files.map(value => value.id === saved.id ? saved : value)
        return saved
      })
      operation = mountedFeed.feed.align('newer', -500)
    }
    else
    {
      vi.spyOn(LiveMarkerAPI, 'upload').mockImplementationOnce(async () =>
      {
        await pending.promise
        state.archive.files.push(saved)
        return saved
      })
      operation = mountedFeed.feed.upload({ file: new File(['Marker: []'], 'new.yaml'), timeZoneId: 'Asia/Shanghai', calibrationMs: -500 })
    }
    mountedFeed.active.value = false
    await nextTick()
    pending.resolve()
    await operation
    expect(state.files).toHaveBeenCalledTimes(1)
    mountedFeed.active.value = true
    await flushPromises()
    expect(state.files).toHaveBeenCalledTimes(2)
    expect(mountedFeed.feed.detail.value?.file.id).toBe(mutation === 'remove' ? 'older' : saved.id)
    if (mutation !== 'remove')
    {
      expect(mountedFeed.feed.detail.value?.entries.map(value => value.offsetMs)).toEqual([500, 1500])
    }
  })

  it('隐藏期间身份范围变化取消旧详情并清空快照，旧响应不能回写', async () =>
  {
    const state = mockArchive([file('current')])
    const pending = deferred<LiveMarkerDetail>()
    state.read.mockReturnValueOnce(pending.promise)
    const mountedFeed = setupFeed()
    await flushPromises()
    mountedFeed.active.value = false
    await nextTick()
    mountedFeed.scope.value = 'viewer-2'
    await nextTick()
    expect(state.read.mock.calls[0]![2]?.aborted).toBe(true)
    expect(mountedFeed.feed.files.value).toEqual([])
    pending.resolve(detail(file('current')))
    await flushPromises()
    expect(mountedFeed.feed.detail.value).toBeUndefined()
    mountedFeed.active.value = true
    await flushPromises()
    expect(state.files).toHaveBeenCalledTimes(2)
    expect(mountedFeed.feed.detail.value?.file.id).toBe('current')
  })

  it('刷新失败保留已加载内容，重试恢复列表并清除错误', async () =>
  {
    const state = mockArchive()
    const mountedFeed = setupFeed()
    await flushPromises()
    const previous = mountedFeed.feed.detail.value
    state.files.mockRejectedValueOnce(new Error('网络暂时不可用'))
    await mountedFeed.feed.refresh()
    expect(mountedFeed.feed.detail.value).toBe(previous)
    expect(mountedFeed.feed.error.value).toBe('网络暂时不可用')
    await mountedFeed.feed.refresh()
    expect(mountedFeed.feed.error.value).toBe('')
    expect(mountedFeed.feed.detail.value?.file.id).toBe('newer')
  })

  it('上传失败可用原参数重试，成功后选中新文件', async () =>
  {
    const state = mockArchive()
    const mountedFeed = setupFeed()
    await flushPromises()
    const saved = file('uploaded', { uploadedAtUtc: '2026-11-13T00:00:30Z', calibrationMs: -1500 })
    const upload = vi.spyOn(LiveMarkerAPI, 'upload').mockRejectedValueOnce(new Error('磁盘暂不可写')).mockImplementationOnce(async () =>
    {
      state.archive.files.push(saved)
      return saved
    })
    const value = { file: new File(['Marker: []'], '高光.yaml'), timeZoneId: 'Asia/Shanghai', calibrationMs: -1500 }
    expect(await mountedFeed.feed.upload(value)).toBeUndefined()
    expect(mountedFeed.feed.actionError.value).toBe('磁盘暂不可写')
    expect(mountedFeed.feed.saving.value).toBe(false)
    expect(mountedFeed.feed.selectedId.value).toBe('newer')
    expect(await mountedFeed.feed.upload(value)).toEqual(saved)
    expect(upload.mock.calls.map(value => value[1])).toEqual([value, value])
    expect(mountedFeed.feed.actionError.value).toBe('')
    expect(mountedFeed.feed.selectedId.value).toBe('uploaded')
    expect(mountedFeed.feed.detail.value?.file.id).toBe('uploaded')
  })

  it('校准更新按服务器结果重载条目，保留负偏移和原始时间', async () =>
  {
    const state = mockArchive([file('current')])
    vi.spyOn(LiveMarkerAPI, 'align').mockImplementation(async (_sessionId, fileId, calibrationMs) =>
    {
      const changed = file(fileId, { calibrationMs })
      state.archive.files = [changed]
      return changed
    })
    const mountedFeed = setupFeed()
    await flushPromises()
    const realDateTime = mountedFeed.feed.detail.value?.entries[0]?.realDateTime
    await mountedFeed.feed.align('current', -2500)
    expect(mountedFeed.feed.detail.value?.file.calibrationMs).toBe(-2500)
    expect(mountedFeed.feed.detail.value?.entries.map(value => value.offsetMs)).toEqual([-1500, -500])
    expect(mountedFeed.feed.detail.value?.entries[0]?.realDateTime).toBe(realDateTime)
    expect(mountedFeed.feed.detail.value?.entries[0]?.liveTime).toBe('00:00:01')
  })

  it('删除当前文件自动选择剩余文件，删除最后一份清空详情', async () =>
  {
    const state = mockArchive()
    vi.spyOn(LiveMarkerAPI, 'remove').mockImplementation(async (_sessionId, fileId) =>
    {
      state.archive.files = state.archive.files.filter(value => value.id !== fileId)
    })
    const mountedFeed = setupFeed()
    await flushPromises()
    expect(await mountedFeed.feed.remove('newer')).toBe(true)
    expect(mountedFeed.feed.selectedId.value).toBe('older')
    expect(mountedFeed.feed.detail.value?.file.id).toBe('older')
    expect(await mountedFeed.feed.remove('older')).toBe(true)
    expect(mountedFeed.feed.selectedId.value).toBe('')
    expect(mountedFeed.feed.files.value).toEqual([])
    expect(mountedFeed.feed.detail.value).toBeUndefined()
  })

  it('已被其他用户删除的详情返回 404 时切换到剩余文件', async () =>
  {
    const state = mockArchive()
    state.read.mockRejectedValueOnce(new ApiError(404, '记录文件不存在'))
    const mountedFeed = setupFeed()
    await flushPromises()
    expect(mountedFeed.feed.selectedId.value).toBe('older')
    expect(mountedFeed.feed.detail.value?.file.id).toBe('older')
    expect(mountedFeed.feed.files.value.map(value => value.id)).toEqual(['older'])
    expect(mountedFeed.feed.loadingEntries.value).toBe(false)
  })

  it('删除失败不移除本地文件，重试已删除的文件视为成功', async () =>
  {
    const state = mockArchive([file('current')])
    const remove = vi.spyOn(LiveMarkerAPI, 'remove').mockRejectedValueOnce(new Error('暂时无法删除'))
    const mountedFeed = setupFeed()
    await flushPromises()
    expect(await mountedFeed.feed.remove('current')).toBeUndefined()
    expect(mountedFeed.feed.selectedId.value).toBe('current')
    expect(mountedFeed.feed.actionError.value).toBe('暂时无法删除')
    state.archive.files = []
    remove.mockRejectedValueOnce(new ApiError(404, '记录文件不存在'))
    expect(await mountedFeed.feed.remove('current')).toBe(true)
    expect(mountedFeed.feed.actionError.value).toBe('')
    expect(mountedFeed.feed.detail.value).toBeUndefined()
  })

  it('卸载取消列表请求，迟到错误不会触发额外请求', async () =>
  {
    const state = mockArchive()
    let fail!: (error: Error) => void
    state.files.mockImplementationOnce(() => new Promise((_resolve, reject) => { fail = reject }))
    const mountedFeed = setupFeed()
    await flushPromises()
    const signal = state.files.mock.calls[0]?.[1] as AbortSignal
    mountedFeed.wrapper.unmount()
    expect(signal.aborted).toBe(true)
    fail(new Error('关闭后才到达的错误'))
    await flushPromises()
    expect(state.files).toHaveBeenCalledTimes(1)
    expect(mountedFeed.feed.error.value).toBe('')
  })
})

describe('直播记录时间输入与边界', () =>
{
  it.each([
    [-1, 1000, false], [0, 1000, true], [999, 1000, true], [1000, 1000, false],
    [1001, 1000, false], [0, 0, false], [NaN, 1000, false], [Infinity, 1000, false], [0, Infinity, false],
  ])('偏移 %s、时长 %s 时可跳转为 %s', (offsetMs, durationMs, expected) =>
  {
    expect(canSeekLiveMarker(offsetMs, durationMs)).toBe(expected)
  })

  it.each([
    [0, 0], ['0', 0], ['-1.250', -1250], [' +2.001 ', 2001],
    ['0.0001', undefined], ['', undefined], ['  ', undefined], [Infinity, undefined], ['NaN', undefined], ['9007199254741', undefined],
  ] as const)('校准秒数 %s 转换为 %s 毫秒', (value, expected) =>
  {
    expect(parseLiveMarkerCalibration(value)).toBe(expected)
  })

  it('显示负偏移和超过二十四小时的偏移，不将它们夹到零', () =>
  {
    expect(formatLiveMarkerOffset(-1)).toBe('−00:00:00.001')
    expect(formatLiveMarkerOffset(-90061001)).toBe('−25:01:01.001')
    expect(formatLiveMarkerOffset(90061001)).toBe('25:01:01.001')
  })
})

describe('直播记录面板', () =>
{
  function openPanel(values: { active?: boolean; editable?: boolean; durationMs?: number; offsetMs?: number } = {}): VueWrapper
  {
    const wrapper = mount(SessionLiveMarkerPanel, {
      props: { sessionId: 'session-1', durationMs: 10000, ...values },
      global: { plugins: [i18n], stubs },
    })
    mounted.push(wrapper)
    return wrapper
  }

  it('只点击定位按钮才滚动并高亮最近可播放条目，不移动视频播放位置', async () =>
  {
    const state = mockArchive([file('current')])
    state.read.mockResolvedValue(detail(file('current'), [
      entry(0, -10), entry(1, 1000), entry(2, 3000), entry(3, 3000), entry(4, 10000),
    ]))
    const wrapper = openPanel({ offsetMs: 2000 })
    await flushPromises()
    expect(scrollToIndex).not.toHaveBeenCalled()
    await wrapper.get('button[aria-label="定位当前播放位置"]').trigger('click')
    expect(scrollToIndex).toHaveBeenCalledExactlyOnceWith(1, 'center')
    expect(wrapper.findAll('.marker-entry[aria-current="true"]').map(row => row.text())).toEqual([expect.stringContaining('打点1')])
    expect(wrapper.emitted('seek')).toBeUndefined()
    await wrapper.setProps({ offsetMs: 3000, active: false })
    await wrapper.setProps({ active: true })
    await flushPromises()
    expect(scrollToIndex).toHaveBeenCalledTimes(1)
    expect(wrapper.get('.marker-entry[aria-current="true"]').text()).toContain('打点1')
    await wrapper.get('button[aria-label="定位当前播放位置"]').trigger('click')
    expect(scrollToIndex).toHaveBeenLastCalledWith(2, 'center')
    expect(wrapper.get('.marker-entry[aria-current="true"]').text()).toContain('打点2')
    expect(state.files).toHaveBeenCalledTimes(1)
    expect(state.read).toHaveBeenCalledTimes(1)
  })

  it('切换记录文件清理上次定位高亮，内容加载不触发滚动', async () =>
  {
    mockArchive()
    const wrapper = openPanel({ offsetMs: 1000 })
    await flushPromises()
    await wrapper.get('button[aria-label="定位当前播放位置"]').trigger('click')
    expect(wrapper.find('.marker-entry[aria-current="true"]').exists()).toBe(true)
    await wrapper.get('select').setValue('older')
    await flushPromises()
    expect(wrapper.find('.marker-entry[aria-current="true"]').exists()).toBe(false)
    expect(scrollToIndex).toHaveBeenCalledTimes(1)
  })

  it('没有录像范围内条目时禁用定位按钮', async () =>
  {
    const state = mockArchive([file('current')])
    state.read.mockResolvedValue(detail(file('current'), [entry(0, -1), entry(1, 10000)]))
    const wrapper = openPanel({ offsetMs: 5000 })
    await flushPromises()
    expect(wrapper.get<HTMLButtonElement>('button[aria-label="定位当前播放位置"]').element.disabled).toBe(true)
    expect(scrollToIndex).not.toHaveBeenCalled()
  })

  it('只允许录像范围内的打点跳转，保留原文和七色标识', async () =>
  {
    const state = mockArchive([file('current', { entryCount: 5 })])
    const text = '<script>原始高光 & 内容</script>'
    state.read.mockResolvedValue(detail(file('current'), [
      entry(0, -1, { markerColor: 'Red' }), entry(1, 0, { markerColor: 'Orange', message: text }),
      entry(2, 5000, { markerColor: 'Green' }), entry(3, 10000, { markerColor: 'Blue' }),
      entry(4, 11000, { markerColor: 'Grey' }),
    ]))
    const wrapper = openPanel()
    await flushPromises()
    const rows = wrapper.findAll<HTMLButtonElement>('.marker-entry')
    expect(rows.map(row => row.element.disabled)).toEqual([true, false, false, true, true])
    expect(wrapper.findAll('.marker-color').map(value => value.classes().find(value => value !== 'marker-color'))).toEqual(['marker-red', 'marker-orange', 'marker-green', 'marker-blue', 'marker-grey'])
    expect(rows[0]!.get('.marker-time').text()).toBe('−00:00')
    expect(rows[0]!.get('.marker-time').attributes('aria-label')).toBe('录像 −00:00:00.001')
    expect(wrapper.text()).toContain('不在当前录像范围内')
    expect(wrapper.find('script').exists()).toBe(false)
    expect(rows[1]!.text()).toContain(text)
    for (const row of rows) { await row.trigger('click') }
    expect(wrapper.emitted('seek')).toEqual([[0], [5000]])
  })

  it('录制时长增长后未来打点变为可跳转，不重新导入或夹到末尾', async () =>
  {
    const state = mockArchive([file('current')])
    state.read.mockResolvedValue(detail(file('current'), [entry(0, 5000)]))
    const wrapper = openPanel({ durationMs: 5000 })
    await flushPromises()
    expect(wrapper.get<HTMLButtonElement>('.marker-entry').element.disabled).toBe(true)
    await wrapper.setProps({ durationMs: 6000 })
    expect(wrapper.get<HTMLButtonElement>('.marker-entry').element.disabled).toBe(false)
    await wrapper.get('.marker-entry').trigger('click')
    expect(wrapper.emitted('seek')).toEqual([[5000]])
    expect(state.read).toHaveBeenCalledTimes(1)
  })

  it('切换同名文件仍按独立 ID 读取，通过 tooltip 查看上传者', async () =>
  {
    const state = mockArchive([
      file('older', { originalFileName: '记录.yaml', uploaderName: '上传者甲' }),
      file('newer', { originalFileName: '记录.yaml', uploaderName: '上传者乙', uploadedAtUtc: '2026-11-13T00:00:20Z' }),
    ])
    const wrapper = openPanel()
    await flushPromises()
    const options = wrapper.findAll('option')
    expect(options.map(value => [value.element.value, value.text()])).toEqual([['newer', '记录.yaml'], ['older', '记录.yaml']])
    const fileSelect = wrapper.findComponent(select)
    expect(fileSelect.props('items')).toMatchObject([{ info: expect.stringContaining('上传者乙') }, { info: expect.stringContaining('上传者甲') }])
    const fileTooltip = wrapper.getComponent(stubs.VTooltip)
    expect(fileTooltip.props('text')).toContain('上传者乙')
    expect(wrapper.text()).not.toContain('上传者乙')
    await wrapper.get('select').setValue('older')
    await flushPromises()
    expect(state.read.mock.calls.at(-1)?.slice(0, 2)).toEqual(['session-1', 'older'])
    expect(wrapper.get('select').element.value).toBe('older')
    expect(fileTooltip.props('text')).toContain('上传者甲')
    expect(wrapper.text()).not.toContain('上传者甲')
  })

  it('管理按钮依赖服务端文件能力，上传入口依赖上传权限', async () =>
  {
    auth.canUploadLiveMarkers = false
    mockArchive([file('shared', { canManage: false })])
    const wrapper = openPanel()
    await flushPromises()
    expect(wrapper.find('button[aria-label="上传记录"]').exists()).toBe(false)
    expect(wrapper.find('button[aria-label="校准时间"]').exists()).toBe(false)
    expect(wrapper.find('button[aria-label="删除记录文件"]').exists()).toBe(false)
    expect(wrapper.find('.marker-entry').exists()).toBe(true)
  })

  it('只读场次隐藏所有修改入口，仍可查看和定位已有记录', async () =>
  {
    mockArchive([file('current')])
    const wrapper = openPanel({ editable: false })
    await flushPromises()
    expect(wrapper.find('button[aria-label="上传记录"]').exists()).toBe(false)
    expect(wrapper.find('button[aria-label="校准时间"]').exists()).toBe(false)
    expect(wrapper.find('button[aria-label="删除记录文件"]').exists()).toBe(false)
    await wrapper.findAll('.marker-entry')[0]!.trigger('click')
    expect(wrapper.emitted('seek')).toEqual([[1000]])
  })

  it.each([
    ['zh-CN', '直播记录', '暂无直播记录', '上传记录'],
    ['en-US', 'Live notes', 'No live notes yet', 'Upload notes'],
    ['ja-JP', '配信記録', '配信記録はまだありません', '記録をアップロード'],
  ] as const)('%s 显示本地化标题、空态和上传入口', async (language, title, empty, uploadLabel) =>
  {
    mockArchive([])
    const wrapper = openPanel()
    await flushPromises()
    i18n.global.locale.value = language
    await nextTick()
    expect(wrapper.get('h2').text()).toBe(title)
    expect(wrapper.text()).toContain(empty)
    expect(wrapper.find(`button[aria-label="${uploadLabel}"]`).exists()).toBe(true)
    expect(wrapper.text()).not.toContain('liveMarkers.')
  })
})

describe('直播记录上传与校准表单', () =>
{
  function openUpload(): VueWrapper
  {
    const wrapper = mount(SessionLiveMarkerUploadDialog, {
      props: { modelValue: true, defaultTimeZoneId: 'Asia/Shanghai', busy: false, error: '' },
      global: { plugins: [i18n], stubs },
    })
    mounted.push(wrapper)
    return wrapper
  }

  async function chooseFile(wrapper: VueWrapper, value = new File(['Marker: []'], '直播记录.yaml')): Promise<File>
  {
    const input = wrapper.get<HTMLInputElement>('input[type="file"]')
    Object.defineProperty(input.element, 'files', { configurable: true, value: [value] })
    await input.trigger('change')
    return value
  }

  it('通过原生表单提交文件，外置主按钮关联同一表单并携带校准毫秒', async () =>
  {
    const wrapper = openUpload()
    const source = await chooseFile(wrapper)
    await wrapper.get('input[type="text"]').setValue(' UTC ')
    await wrapper.get('input[type="number"]').setValue('-1.250')
    const form = wrapper.get('form')
    expect(wrapper.get('button[type="submit"]').attributes('form')).toBe(form.attributes('id'))
    await form.trigger('submit')
    expect(wrapper.emitted('upload')).toEqual([[{ file: source, timeZoneId: 'UTC', calibrationMs: -1250 }]])
  })

  it('提交失败保留文件、来源时区和校准草稿，直接再次提交', async () =>
  {
    const wrapper = openUpload()
    const source = await chooseFile(wrapper)
    await wrapper.get('input[type="text"]').setValue('UTC')
    await wrapper.get('input[type="number"]').setValue('2.125')
    await wrapper.get('form').trigger('submit')
    await wrapper.setProps({ busy: true })
    expect(wrapper.findComponent(dialog).props('persistent')).toBe(true)
    expect(wrapper.findAll('button').every(value => (value.element as HTMLButtonElement).disabled)).toBe(true)
    await wrapper.get('form').trigger('submit')
    expect(wrapper.emitted('upload')).toHaveLength(1)
    await wrapper.setProps({ busy: false, error: '暂时无法写入文件' })
    expect(wrapper.findComponent(fileInput).props('modelValue')).toBe(source)
    expect(wrapper.get<HTMLInputElement>('input[type="text"]').element.value).toBe('UTC')
    expect(wrapper.get<HTMLInputElement>('input[type="number"]').element.value).toBe('2.125')
    expect(wrapper.text()).toContain('暂时无法写入文件')
    await wrapper.get('form').trigger('submit')
    expect(wrapper.emitted('upload')).toHaveLength(2)
    expect(wrapper.emitted('update:modelValue')).toBeUndefined()
  })

  it('拒绝非 YAML 文件与不足毫秒精度的校准，不发出上传事件', async () =>
  {
    const wrapper = openUpload()
    await chooseFile(wrapper, new File(['text'], '记录.txt'))
    await wrapper.get('form').trigger('submit')
    expect(wrapper.emitted('upload')).toBeUndefined()
    expect(wrapper.text()).toContain('请选择 5 MiB 以内的 YAML / YML 文件')
    await chooseFile(wrapper)
    await wrapper.get('input[type="number"]').setValue('0.0001')
    await wrapper.get('form').trigger('submit')
    expect(wrapper.emitted('upload')).toBeUndefined()
    expect(wrapper.text()).toContain('最多三位小数')
  })

  it('关闭后重新打开清理旧文件和校准，并使用最新站点默认时区', async () =>
  {
    const wrapper = openUpload()
    await chooseFile(wrapper)
    await wrapper.get('input[type="number"]').setValue('3')
    await wrapper.setProps({ modelValue: false, defaultTimeZoneId: 'UTC' })
    await wrapper.setProps({ modelValue: true })
    expect(wrapper.findComponent(fileInput).props('modelValue')).toBeNull()
    expect(wrapper.get<HTMLInputElement>('input[type="text"]').element.value).toBe('UTC')
    expect(wrapper.get<HTMLInputElement>('input[type="number"]').element.value).toBe('0')
  })

  it('校准弹窗初始显示已有秒数，提交和失败重试均保留毫秒精度', async () =>
  {
    const wrapper = mount(SessionLiveMarkerAlignmentDialog, {
      props: { modelValue: true, file: file('current', { calibrationMs: -1500 }), busy: false, error: '' },
      global: { plugins: [i18n], stubs },
    })
    mounted.push(wrapper)
    const form = wrapper.get('form')
    expect(wrapper.get<HTMLInputElement>('input[type="number"]').element.value).toBe('-1.5')
    expect(wrapper.get('button[type="submit"]').attributes('form')).toBe(form.attributes('id'))
    await wrapper.get('input[type="number"]').setValue('3.125')
    await form.trigger('submit')
    expect(wrapper.emitted('save')).toEqual([[3125]])
    await wrapper.setProps({ error: '校准保存失败' })
    expect(wrapper.get<HTMLInputElement>('input[type="number"]').element.value).toBe('3.125')
    await form.trigger('submit')
    expect(wrapper.emitted('save')).toEqual([[3125], [3125]])
  })
})
