// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { defineComponent, h, ref } from 'vue'
import type { HlsConfig } from 'hls.js'
import { ApiError } from '../src/apis/APIBase'
import { usePlaybackController } from '../src/composables/usePlaybackController'
import MediaPlayer from '../src/components/player/MediaPlayer.vue'
import EventFeed from '../src/components/player/EventFeed.vue'
import type { PlaybackLoadOptions } from '../src/models/playbackLoadOptions'
import type { PlaybackWindow, TimelineGap } from '../src/models/media'
import { i18n } from '../src/plugins/i18n'

interface HlsDouble
{
  config: Partial<HlsConfig>
  loadingEnabled: boolean
  bufferingEnabled: boolean
  startLoad: ReturnType<typeof vi.fn>
  stopLoad: ReturnType<typeof vi.fn>
  pauseBuffering: ReturnType<typeof vi.fn>
  resumeBuffering: ReturnType<typeof vi.fn>
  destroy: ReturnType<typeof vi.fn>
  attachMedia: ReturnType<typeof vi.fn>
  loadSource: ReturnType<typeof vi.fn>
  emit: (event: string, data?: unknown) => void
}

const hlsState = vi.hoisted(() => ({ supported: true, instances: [] as HlsDouble[] }))
const eventsRequest = vi.hoisted(() => vi.fn())
vi.mock('../src/apis/MediaAPI', () => ({ MediaAPI: { playbackEvents: eventsRequest, eventSnapshot: eventsRequest } }))
vi.mock('../src/stores/auth', () => ({ useAuthStore: () => ({ user: { id: 'playback-test-user' }, canViewRecordings: true }) }))
vi.mock('hls.js', () => ({ default: class
{
  static isSupported() { return hlsState.supported }
  static Events = { MANIFEST_PARSED: 'manifest', INIT_PTS_FOUND: 'initPts', ERROR: 'error' }
  loadingEnabled = true
  bufferingEnabled = true
  listeners = new Map<string, (event: string, data?: unknown) => void>()
  startLoad = vi.fn(() => { this.loadingEnabled = true; this.bufferingEnabled = true })
  stopLoad = vi.fn(() => { this.loadingEnabled = false })
  pauseBuffering = vi.fn(() => { this.bufferingEnabled = false })
  resumeBuffering = vi.fn(() => { this.bufferingEnabled = true })
  destroy = vi.fn()
  attachMedia = vi.fn()
  loadSource = vi.fn()
  constructor(readonly config: Partial<HlsConfig>) { hlsState.instances.push(this) }
  on(event: string, callback: (event: string, data?: unknown) => void) { this.listeners.set(event, callback) }
  off(event: string, callback: (event: string, data?: unknown) => void) { if (this.listeners.get(event) === callback) { this.listeners.delete(event) } }
  emit(event: string, data?: unknown) { this.listeners.get(event)?.(event, data) }
} }))

const mounted: VueWrapper[] = []
let tick: FrameRequestCallback
let visibility: DocumentVisibilityState

function playback(startOffsetMs = 0, endOffsetMs = startOffsetMs + 30000): PlaybackWindow
{
  return { url: `/api/playback/window-${startOffsetMs}/index.m3u8`, startOffsetMs, endOffsetMs, requestedOffsetMs: startOffsetMs, isGap: false, durationMs: 120000 }
}

function deferred<T>()
{
  let resolve!: (value: T) => void
  let reject!: (error: unknown) => void
  const promise = new Promise<T>((resolvePromise, rejectPromise) => { resolve = resolvePromise; reject = rejectPromise })
  return { promise, resolve, reject }
}

function setupPlayer(loadWindow = vi.fn<(offsetMs: number, options?: PlaybackLoadOptions) => Promise<PlaybackWindow | undefined>>().mockResolvedValue(playback()), options: { durationMs?: number; initialOffsetMs?: number } = {})
{
  const video = ref<HTMLVideoElement>()
  const currentMs = ref(0)
  const durationMs = ref(options.durationMs ?? 120000)
  const gaps = ref<TimelineGap[]>([])
  const scrubbing = ref(false)
  let controller!: ReturnType<typeof usePlaybackController>
  const wrapper = mount(defineComponent({ setup()
  {
    controller = usePlaybackController({ video, currentMs, scrubbing, durationMs: () => durationMs.value, gaps: () => gaps.value, isLive: () => false, initialOffsetMs: () => options.initialOffsetMs ?? 0, loadWindow })
    return () => h('video', { ref: video })
  } }), { global: { plugins: [i18n] } })
  mounted.push(wrapper)
  return { controller, video, currentMs, durationMs, gaps, scrubbing, loadWindow, wrapper }
}

function setupControlledPlayer(initialOffsetMs = 0)
{
  const offsetMs = ref(0)
  const loadWindow = vi.fn<(offsetMs: number, options?: PlaybackLoadOptions) => Promise<PlaybackWindow | undefined>>().mockResolvedValue(playback())
  const wrapper = mount(defineComponent({ setup: () => () => h(MediaPlayer, {
    modelValue: offsetMs.value,
    'onUpdate:modelValue': (value: number) => { offsetMs.value = value },
    durationMs: 120000,
    gaps: [{ startMs: 40000, endMs: 50000 }],
    initialOffsetMs,
    isLive: false,
    loadWindow,
  }) }), { global: { plugins: [i18n], stubs: { VBtn: true, VSlider: true, VMenu: true, VList: true, VListItem: true, VIcon: true, VSpacer: true, VProgressCircular: true } } })
  mounted.push(wrapper)
  return { offsetMs, loadWindow, wrapper, player: wrapper.findComponent(MediaPlayer), element: wrapper.get('video').element }
}

function setupPlayerWithEvents()
{
  const offsetMs = ref(0)
  const navigationRevision = ref(0)
  const loadWindow = vi.fn(async (value: number) => playback(value))
  const wrapper = mount(defineComponent({ setup: () => () => h('div', [
    h(MediaPlayer, {
      modelValue: offsetMs.value,
      'onUpdate:modelValue': (value: number) => { offsetMs.value = value },
      durationMs: 120000,
      gaps: [],
      isLive: false,
      loadWindow,
      onNavigate: () => { navigationRevision.value++ },
    }),
    h(EventFeed, { sessionId: 's1', offsetMs: offsetMs.value, enabled: true, isLive: false, navigationRevision: navigationRevision.value }),
  ]) }), {
    global: {
      plugins: [i18n],
      stubs: { VCard: { template: '<div><slot /></div>' }, VVirtualScroll: true, VBtn: true, VSlider: true, VMenu: true, VList: true, VListItem: true, VIcon: true, VSpacer: true, VProgressCircular: true, VProgressLinear: true, VChipGroup: true, VChip: true, VAlert: true },
    },
  })
  mounted.push(wrapper)
  return { offsetMs, loadWindow, wrapper, player: wrapper.findComponent(MediaPlayer), feed: wrapper.findComponent(EventFeed), element: wrapper.get('video').element }
}

function ready(state: ReturnType<typeof setupPlayer>): void
{
  readyVideo(state.video.value!)
}

function readyVideo(element: HTMLVideoElement): void
{
  Object.defineProperty(element, 'readyState', { configurable: true, value: 4 })
  element.dispatchEvent(new Event('loadedmetadata'))
  element.dispatchEvent(new Event('seeked'))
  element.dispatchEvent(new Event('canplay'))
}

async function nearEnd(state: ReturnType<typeof setupPlayer>, seconds = 21): Promise<void>
{
  ready(state)
  await state.controller.togglePlay()
  state.video.value!.currentTime = seconds
  tick(1000)
  await flushPromises()
}

beforeEach(() =>
{
  hlsState.supported = true
  hlsState.instances.length = 0
  eventsRequest.mockReset()
  visibility = 'visible'
  vi.spyOn(document, 'visibilityState', 'get').mockImplementation(() => visibility)
  vi.spyOn(HTMLMediaElement.prototype, 'pause').mockImplementation(() => {})
  vi.spyOn(HTMLMediaElement.prototype, 'play').mockResolvedValue()
  vi.spyOn(HTMLMediaElement.prototype, 'load').mockImplementation(() => {})
  vi.spyOn(HTMLMediaElement.prototype, 'canPlayType').mockReturnValue('probably')
  vi.stubGlobal('requestAnimationFrame', vi.fn((callback: FrameRequestCallback) => { tick = callback; return 1 }))
  vi.stubGlobal('cancelAnimationFrame', vi.fn())
  vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
})
afterEach(() =>
{
  mounted.splice(0).forEach(wrapper => wrapper.unmount())
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
  vi.useRealTimers()
})

describe('视频与弹幕独立加载', () =>
{
  it.each(['hlsjs', 'native'])('%s 弹幕持续加载时视频独立完成首帧、播放与跳转', async mode =>
  {
    hlsState.supported = mode === 'hlsjs'
    eventsRequest.mockImplementation(() => new Promise(() => {}))
    const state = setupPlayerWithEvents()
    await flushPromises()
    expect(state.loadWindow).toHaveBeenCalledTimes(1)
    expect(state.feed.find('v-progress-linear-stub').exists()).toBe(true)

    readyVideo(state.element)
    await flushPromises()
    expect(state.player.find('v-progress-circular-stub').exists()).toBe(false)
    await state.player.get('.video-stage').trigger('click')
    state.element.currentTime = 6
    tick(1000)
    await flushPromises()
    expect(state.offsetMs.value).toBe(6000)
    expect(HTMLMediaElement.prototype.play).toHaveBeenCalled()
    expect(eventsRequest).toHaveBeenCalledTimes(1)
    expect(state.feed.find('v-progress-linear-stub').exists()).toBe(true)

    await state.player.vm.seek(45000)
    await flushPromises()
    readyVideo(state.element)
    await flushPromises()
    expect(state.loadWindow).toHaveBeenCalledTimes(2)
    expect(state.offsetMs.value).toBe(45000)
    expect(state.player.find('v-progress-circular-stub').exists()).toBe(false)
    expect(state.feed.find('v-progress-linear-stub').exists()).toBe(true)
  })

  it.each(['hlsjs', 'native'])('%s 弹幕读取失败时视频仍能显示画面与播放', async mode =>
  {
    hlsState.supported = mode === 'hlsjs'
    eventsRequest.mockRejectedValue(new Error('弹幕暂时无法读取'))
    const state = setupPlayerWithEvents()
    await flushPromises()
    expect(state.feed.find('v-alert-stub').exists()).toBe(true)

    readyVideo(state.element)
    await state.player.get('.video-stage').trigger('click')
    state.element.currentTime = 8
    tick(1000)
    await flushPromises()
    expect(state.offsetMs.value).toBe(8000)
    expect(state.player.find('.error-overlay').exists()).toBe(false)
    expect(state.player.find('v-progress-circular-stub').exists()).toBe(false)
    expect(HTMLMediaElement.prototype.play).toHaveBeenCalled()
  })
})

describe('暂停预览的媒体预读', () =>
{
  it('首次暂停预览拿到当前帧即停止后续片段，不必等待 canplay', async () =>
  {
    const state = setupPlayer()
    await flushPromises()
    const instance = hlsState.instances[0]!
    expect(instance.config.maxBufferLength).toBe(1)
    expect(instance.config.maxMaxBufferLength).toBe(1)
    expect(instance.bufferingEnabled).toBe(true)
    expect(state.controller.loading.value).toBe(true)
    Object.defineProperty(state.video.value!, 'readyState', { configurable: true, value: 1 })
    state.video.value!.dispatchEvent(new Event('loadedmetadata'))
    expect(instance.bufferingEnabled).toBe(true)
    Object.defineProperty(state.video.value!, 'readyState', { configurable: true, value: 2 })
    state.video.value!.dispatchEvent(new Event('loadeddata'))
    expect(state.controller.loading.value).toBe(false)
    expect(instance.bufferingEnabled).toBe(false)
    state.video.value!.dispatchEvent(new Event('canplay'))
    state.video.value!.dispatchEvent(new Event('seeked'))
    expect(instance.bufferingEnabled).toBe(false)
    expect(instance.stopLoad).not.toHaveBeenCalled()
    expect(HTMLMediaElement.prototype.play).not.toHaveBeenCalled()
  })

  it('播放恢复预读，暂停保留在途片与媒体源，后台播放继续缓冲', async () =>
  {
    const state = setupPlayer()
    await flushPromises()
    ready(state)
    const instance = hlsState.instances[0]!
    expect(instance.bufferingEnabled).toBe(false)
    instance.resumeBuffering.mockImplementationOnce(() =>
    {
      expect(instance.config.maxBufferLength).toBe(30)
      expect(instance.config.maxMaxBufferLength).toBe(60)
      instance.bufferingEnabled = true
    })
    await state.controller.togglePlay()
    expect(instance.bufferingEnabled).toBe(true)
    instance.config.maxMaxBufferLength = 20
    state.video.value!.dispatchEvent(new Event('canplay'))
    expect(instance.config.maxMaxBufferLength).toBe(20)
    visibility = 'hidden'
    document.dispatchEvent(new Event('visibilitychange'))
    expect(instance.bufferingEnabled).toBe(true)
    await state.controller.togglePlay()
    expect(instance.bufferingEnabled).toBe(false)
    expect(instance.loadingEnabled).toBe(true)
    expect(instance.config.maxBufferLength).toBe(1)
    expect(instance.config.maxMaxBufferLength).toBe(1)
    await state.controller.togglePlay()
    expect(instance.bufferingEnabled).toBe(true)
    expect(instance.config.maxBufferLength).toBe(30)
    expect(instance.config.maxMaxBufferLength).toBe(60)
    expect(instance.config.backBufferLength).toBe(60)
    expect(instance.config.frontBufferFlushThreshold).toBe(60)
    expect(instance.stopLoad).not.toHaveBeenCalled()
    expect(instance.startLoad).not.toHaveBeenCalled()
    expect(instance.destroy).not.toHaveBeenCalled()
    expect(state.loadWindow).toHaveBeenCalledTimes(1)
  })

  it('首帧准备中先播放再暂停，仍完成目标预览后停止预读', async () =>
  {
    const state = setupPlayer()
    await flushPromises()
    const instance = hlsState.instances[0]!
    await state.controller.togglePlay()
    expect(instance.config.maxBufferLength).toBe(30)
    expect(instance.config.maxMaxBufferLength).toBe(60)
    await state.controller.togglePlay()
    expect(state.controller.playing.value).toBe(false)
    expect(instance.config.maxBufferLength).toBe(1)
    expect(instance.config.maxMaxBufferLength).toBe(1)
    expect(instance.bufferingEnabled).toBe(true)
    ready(state)
    expect(state.controller.loading.value).toBe(false)
    expect(instance.bufferingEnabled).toBe(false)
    expect(HTMLMediaElement.prototype.play).not.toHaveBeenCalled()
  })

  it('暂停跳转恢复目标所需加载，旧就绪事件不能提前停载，目标到位后再次暂停预读', async () =>
  {
    const state = setupPlayer()
    await flushPromises()
    ready(state)
    const instance = hlsState.instances[0]!
    expect(instance.bufferingEnabled).toBe(false)
    instance.resumeBuffering.mockImplementationOnce(() =>
    {
      expect(instance.config.maxBufferLength).toBe(1)
      expect(instance.config.maxMaxBufferLength).toBe(1)
      instance.bufferingEnabled = true
    })
    Object.defineProperty(state.video.value!, 'readyState', { configurable: true, value: 1 })
    await state.controller.seek(12000)
    expect(instance.bufferingEnabled).toBe(true)
    expect(state.controller.loading.value).toBe(true)
    Object.defineProperty(state.video.value!, 'readyState', { configurable: true, value: 4 })
    state.video.value!.currentTime = 2
    state.video.value!.dispatchEvent(new Event('canplay'))
    expect(instance.bufferingEnabled).toBe(true)
    expect(state.controller.loading.value).toBe(true)
    state.video.value!.currentTime = 12
    state.video.value!.dispatchEvent(new Event('seeked'))
    expect(instance.bufferingEnabled).toBe(false)
    expect(state.controller.loading.value).toBe(false)
    expect(state.currentMs.value).toBe(12000)
    expect(state.loadWindow).toHaveBeenCalledTimes(1)
    expect(instance.startLoad).not.toHaveBeenCalled()
    expect(HTMLMediaElement.prototype.play).not.toHaveBeenCalled()
  })

  it.each(['hlsjs', 'native'])('%s 预览录像不足一秒的尾部，目标帧可用即可完成并恢复播放', async mode =>
  {
    hlsState.supported = mode === 'hlsjs'
    const durationMs = 10250
    const loadWindow = vi.fn<(offsetMs: number, options?: PlaybackLoadOptions) => Promise<PlaybackWindow | undefined>>()
      .mockResolvedValue({ ...playback(0, durationMs), durationMs })
    const state = setupPlayer(loadWindow, { durationMs, initialOffsetMs: 10000 })
    await flushPromises()
    const instance = hlsState.instances[0]
    if (instance)
    {
      expect(instance.config.maxBufferLength).toBe(1)
      expect(instance.config.maxMaxBufferLength).toBe(1)
    }
    Object.defineProperty(state.video.value!, 'readyState', { configurable: true, value: 1 })
    state.video.value!.dispatchEvent(new Event('loadedmetadata'))
    expect(state.video.value!.currentTime).toBe(10)
    expect(state.controller.loading.value).toBe(true)
    Object.defineProperty(state.video.value!, 'readyState', { configurable: true, value: 2 })
    state.video.value!.dispatchEvent(new Event('seeked'))
    expect(state.controller.loading.value).toBe(false)
    expect(state.controller.playing.value).toBe(false)
    if (instance) { expect(instance.bufferingEnabled).toBe(false) }
    await state.controller.togglePlay()
    expect(state.controller.playing.value).toBe(true)
    expect(HTMLMediaElement.prototype.play).toHaveBeenCalled()
    if (instance)
    {
      expect(instance.bufferingEnabled).toBe(true)
      expect(instance.config.maxBufferLength).toBe(30)
      expect(instance.config.maxMaxBufferLength).toBe(60)
      expect(instance.stopLoad).not.toHaveBeenCalled()
    }
    expect(loadWindow).toHaveBeenCalledTimes(1)
  })

  it('暂停时重复定位已显示画面，不依赖新媒体事件也不恢复预读', async () =>
  {
    const state = setupPlayer()
    await flushPromises()
    ready(state)
    const instance = hlsState.instances[0]!
    const resumeCount = instance.resumeBuffering.mock.calls.length
    await state.controller.seek(0)
    expect(state.controller.loading.value).toBe(false)
    expect(instance.bufferingEnabled).toBe(false)
    expect(instance.resumeBuffering.mock.calls.length).toBe(resumeCount)
    expect(state.loadWindow).toHaveBeenCalledTimes(1)
  })

  it.each(['hlsjs', 'native'])('%s 实际媒体短于全场时间时，尾部预览按同一媒体边界完成并响应时长更新', async mode =>
  {
    hlsState.supported = mode === 'hlsjs'
    const durationMs = 95288
    const loadWindow = vi.fn<(offsetMs: number, options?: PlaybackLoadOptions) => Promise<PlaybackWindow | undefined>>()
      .mockResolvedValue({ ...playback(0, durationMs), durationMs })
    const state = setupPlayer(loadWindow, { durationMs, initialOffsetMs: 95287 })
    await flushPromises()
    const element = state.video.value!
    let mediaDuration = 95
    let mediaTime = 19
    Object.defineProperty(element, 'duration', { configurable: true, get: () => mediaDuration })
    Object.defineProperty(element, 'currentTime', { configurable: true, get: () => mediaTime, set: value => { mediaTime = Math.min(value, mediaDuration) } })
    ready(state)
    expect(element.currentTime).toBeCloseTo(94.999, 6)
    expect(state.controller.loading.value).toBe(false)
    expect(state.currentMs.value).toBe(95287)
    mediaDuration = 95.14
    element.dispatchEvent(new Event('durationchange'))
    element.dispatchEvent(new Event('seeked'))
    expect(element.currentTime).toBeCloseTo(95.139, 6)
    expect(state.controller.loading.value).toBe(false)
    expect(state.currentMs.value).toBe(95287)
    expect(loadWindow).toHaveBeenCalledTimes(1)
    if (mode === 'hlsjs') { expect(hlsState.instances[0]!.bufferingEnabled).toBe(false) }
    await state.controller.togglePlay()
    Object.defineProperty(element, 'ended', { configurable: true, value: true })
    element.dispatchEvent(new Event('ended'))
    await flushPromises()
    expect(state.currentMs.value).toBe(durationMs)
    expect(state.controller.playing.value).toBe(false)
    expect(loadWindow).toHaveBeenCalledTimes(1)
  })

  it('冷跳不受局部seekable或buffered范围限制，未知媒体时长也不截断目标', async () =>
  {
    const state = setupPlayer(vi.fn().mockResolvedValue(playback(0, 120000)), { initialOffsetMs: 50000 })
    await flushPromises()
    const element = state.video.value!
    const partial = { length: 1, start: () => 0, end: () => 6 }
    Object.defineProperty(element, 'seekable', { configurable: true, value: partial })
    Object.defineProperty(element, 'buffered', { configurable: true, value: partial })
    Object.defineProperty(element, 'duration', { configurable: true, value: 120 })
    ready(state)
    expect(element.currentTime).toBe(50)
    Object.defineProperty(element, 'duration', { configurable: true, value: Number.POSITIVE_INFINITY })
    await state.controller.seek(90000)
    ready(state)
    expect(element.currentTime).toBe(90)
    expect(state.loadWindow).toHaveBeenCalledTimes(1)
  })

  it('时长在定位之后才确定时重新约束目标，正常播放中的durationchange不会回退旧目标', async () =>
  {
    const state = setupPlayer(vi.fn().mockResolvedValue(playback(0, 120000)), { initialOffsetMs: 100000 })
    await flushPromises()
    const element = state.video.value!
    Object.defineProperty(element, 'readyState', { configurable: true, value: 1 })
    element.dispatchEvent(new Event('loadedmetadata'))
    expect(element.currentTime).toBe(100)
    Object.defineProperty(element, 'duration', { configurable: true, value: 95 })
    element.currentTime = 95
    Object.defineProperty(element, 'readyState', { configurable: true, value: 2 })
    element.dispatchEvent(new Event('durationchange'))
    expect(state.controller.loading.value).toBe(false)
    expect(state.currentMs.value).toBe(100000)
    await state.controller.seek(10000)
    ready(state)
    await state.controller.togglePlay()
    element.currentTime = 20
    tick(1000)
    Object.defineProperty(element, 'duration', { configurable: true, value: 96 })
    element.dispatchEvent(new Event('durationchange'))
    expect(element.currentTime).toBe(20)
    expect(state.currentMs.value).toBe(20000)
    expect(state.controller.loading.value).toBe(false)
  })

  it('恢复播放时即使等待数据也先恢复加载，不会被 loading 状态挡住', async () =>
  {
    const state = setupPlayer()
    await flushPromises()
    ready(state)
    const instance = hlsState.instances[0]!
    Object.defineProperty(state.video.value!, 'readyState', { configurable: true, value: 1 })
    state.video.value!.dispatchEvent(new Event('waiting'))
    expect(state.controller.loading.value).toBe(true)
    expect(instance.bufferingEnabled).toBe(false)
    await state.controller.togglePlay()
    expect(instance.bufferingEnabled).toBe(true)
    expect(HTMLMediaElement.prototype.play).not.toHaveBeenCalled()
    ready(state)
    expect(state.controller.loading.value).toBe(false)
    expect(state.controller.playing.value).toBe(true)
    expect(HTMLMediaElement.prototype.play).toHaveBeenCalled()
  })

  it('有效播放请求被拒后停止追加预读，终态错误停止加载且迟到就绪事件不能重启', async () =>
  {
    const state = setupPlayer()
    await flushPromises()
    ready(state)
    const instance = hlsState.instances[0]!
    vi.mocked(HTMLMediaElement.prototype.play).mockRejectedValueOnce(new DOMException('Denied', 'NotAllowedError'))
    await state.controller.togglePlay()
    await flushPromises()
    expect(state.controller.playing.value).toBe(false)
    expect(instance.bufferingEnabled).toBe(false)
    expect(instance.stopLoad).not.toHaveBeenCalled()
    await state.controller.togglePlay()
    instance.emit('error', { fatal: true })
    expect(instance.loadingEnabled).toBe(false)
    ready(state)
    expect(instance.loadingEnabled).toBe(false)
    expect(state.controller.error.value).not.toBe('')
  })
})

describe('播放源定位与请求取消', () =>
{
  it('连续长录像经过内部片段边界时保留播放源，并限制前后缓冲', async () =>
  {
    const durationMs = 3 * 60 * 60 * 1000
    const loadWindow = vi.fn<(offsetMs: number, options?: PlaybackLoadOptions) => Promise<PlaybackWindow | undefined>>()
      .mockResolvedValue({ ...playback(0, durationMs), durationMs })
    const state = setupPlayer(loadWindow, { durationMs })
    await flushPromises()
    ready(state)
    await state.controller.togglePlay()
    const instance = hlsState.instances[0]!
    for (const seconds of [29.99, 30.01, 59.99, 60.01, 3599, 3600, 7200])
    {
      state.video.value!.currentTime = seconds
      tick(seconds * 1000)
      await flushPromises()
      expect(state.currentMs.value).toBeCloseTo(seconds * 1000)
      expect(state.controller.loading.value).toBe(false)
    }
    expect(loadWindow).toHaveBeenCalledTimes(1)
    expect(hlsState.instances).toHaveLength(1)
    expect(instance.destroy).not.toHaveBeenCalled()
    expect(instance.stopLoad).not.toHaveBeenCalled()
    for (const length of [instance.config.maxBufferLength, instance.config.maxMaxBufferLength, instance.config.backBufferLength, instance.config.frontBufferFlushThreshold])
    {
      expect(Number.isFinite(length)).toBe(true)
      expect(length).toBeGreaterThan(0)
      expect(length).toBeLessThan(durationMs / 1000)
    }
    expect(instance.config.maxMaxBufferLength).toBeGreaterThanOrEqual(instance.config.maxBufferLength!)
  })

  it.each(['hlsjs', 'native'])('%s 在长录像内连续深跳转保留同一源、正确映射时间并等待新位置就绪', async mode =>
  {
    hlsState.supported = mode === 'hlsjs'
    const durationMs = 4 * 60 * 60 * 1000
    const startOffsetMs = 60000
    const loadWindow = vi.fn<(offsetMs: number, options?: PlaybackLoadOptions) => Promise<PlaybackWindow | undefined>>()
      .mockResolvedValue({ ...playback(startOffsetMs, durationMs), durationMs })
    const state = setupPlayer(loadWindow, { durationMs, initialOffsetMs: startOffsetMs + 5000 })
    await flushPromises()
    ready(state)
    const instance = hlsState.instances[0]
    const nativeLoads = vi.mocked(HTMLMediaElement.prototype.load).mock.calls.length
    state.controller.rate.value = 1.5
    state.controller.volume.value = 0.4
    const element = state.video.value!
    Object.defineProperty(element, 'readyState', { configurable: true, value: 1 })
    Object.defineProperty(element, 'seeking', { configurable: true, value: true })
    const older = state.controller.seek(60 * 60 * 1000)
    const targetMs = 2 * 60 * 60 * 1000
    const newer = state.controller.seek(targetMs)
    await Promise.all([older, newer])
    element.dispatchEvent(new Event('canplay'))
    expect(state.controller.loading.value).toBe(true)
    expect(state.currentMs.value).toBe(targetMs)
    expect(element.currentTime).toBe((targetMs - startOffsetMs) / 1000)
    Object.defineProperty(element, 'seeking', { configurable: true, value: false })
    ready(state)
    expect(state.controller.loading.value).toBe(false)
    expect(state.controller.playing.value).toBe(false)
    expect(element.playbackRate).toBe(1.5)
    expect(element.volume).toBe(0.4)
    expect(loadWindow).toHaveBeenCalledTimes(1)
    expect(hlsState.instances).toHaveLength(mode === 'hlsjs' ? 1 : 0)
    if (instance) { expect(instance.destroy).not.toHaveBeenCalled() }
    expect(vi.mocked(HTMLMediaElement.prototype.load).mock.calls.length).toBe(nativeLoads)
    expect(HTMLMediaElement.prototype.play).not.toHaveBeenCalled()
  })

  it('真实父组件 v-model 从空档返回已安装源时，不依赖额外媒体事件完成暂停定位', async () =>
  {
    const { offsetMs, loadWindow, wrapper, player, element } = setupControlledPlayer()
    await flushPromises()
    readyVideo(element)
    await player.vm.seek(45000)
    expect(offsetMs.value).toBe(45000)
    expect(wrapper.find('.gap-overlay').exists()).toBe(true)
    await player.vm.seek(15000)
    await flushPromises()
    expect(offsetMs.value).toBe(15000)
    expect(element.currentTime).toBe(15)
    expect(loadWindow).toHaveBeenCalledTimes(1)
    expect(hlsState.instances).toHaveLength(1)
    expect(HTMLMediaElement.prototype.play).not.toHaveBeenCalled()
    await player.vm.seek(45000)
    const older = player.vm.seek(10000)
    const newer = player.vm.seek(20000)
    await Promise.all([older, newer])
    expect(offsetMs.value).toBe(20000)
    expect(element.currentTime).toBe(20)
    expect(loadWindow).toHaveBeenCalledTimes(1)
  })

  it('用户跳到全场末尾会加载最后物理段并定位末尾帧，仍只做暂停预览', async () =>
  {
    const { offsetMs, loadWindow, player, element } = setupControlledPlayer()
    await flushPromises()
    readyVideo(element)
    await player.vm.seek(19000)
    loadWindow.mockResolvedValueOnce(playback(90000, 120000))
    await player.vm.seek(120000)
    readyVideo(element)
    expect(offsetMs.value).toBe(119999)
    expect(loadWindow.mock.calls.at(-1)?.[0]).toBe(119999)
    expect(element.currentTime).toBe(29.999)
    expect(hlsState.instances).toHaveLength(2)
    expect(hlsState.instances[1]!.config.startPosition).toBe(29.999)
    expect(hlsState.instances[1]!.config.maxBufferLength).toBe(1)
    expect(hlsState.instances[1]!.pauseBuffering).toHaveBeenCalled()
    expect(HTMLMediaElement.prototype.play).not.toHaveBeenCalled()
  })

  it('用户在断流起止边界导航不会偏移，跨出空档正常加载对应分段', async () =>
  {
    const { offsetMs, loadWindow, wrapper, player, element } = setupControlledPlayer()
    await flushPromises()
    readyVideo(element)
    await player.vm.seek(40000)
    expect(offsetMs.value).toBe(40000)
    expect(wrapper.find('.gap-overlay').exists()).toBe(true)
    expect(loadWindow).toHaveBeenCalledTimes(1)
    loadWindow.mockResolvedValueOnce(playback(50000, 80000))
    await player.vm.seek(50000)
    readyVideo(element)
    await flushPromises()
    expect(offsetMs.value).toBe(50000)
    expect(wrapper.find('.gap-overlay').exists()).toBe(false)
    expect(element.currentTime).toBe(0)
    expect(loadWindow.mock.calls.at(-1)?.[0]).toBe(50000)
  })

  it('自动播放到录像末尾仍停止播放，不重新加载末尾预览', async () =>
  {
    const loadWindow = vi.fn<(offsetMs: number, options?: PlaybackLoadOptions) => Promise<PlaybackWindow | undefined>>().mockResolvedValue(playback(0, 30000))
    const state = setupPlayer(loadWindow, { durationMs: 30000 })
    await flushPromises()
    ready(state)
    await state.controller.togglePlay()
    state.video.value!.currentTime = 30
    tick(1000)
    await flushPromises()
    expect(state.currentMs.value).toBe(30000)
    expect(state.controller.playing.value).toBe(false)
    expect(loadWindow).toHaveBeenCalledTimes(1)
    expect(hlsState.instances).toHaveLength(1)
  })

  it('真实父组件初始定位空档后自动跨出空档，只加载一次新窗口并继续播放', async () =>
  {
    const { offsetMs, loadWindow, wrapper, element } = setupControlledPlayer(45000)
    await flushPromises()
    expect(offsetMs.value).toBe(45000)
    expect(wrapper.find('.gap-overlay').exists()).toBe(true)
    expect(loadWindow).not.toHaveBeenCalled()
    loadWindow.mockResolvedValueOnce(playback(50000, 80000))
    await wrapper.get('v-btn-stub[icon="mdi-play"]').trigger('click')
    tick(1000)
    tick(6000)
    await flushPromises()
    readyVideo(element)
    await flushPromises()
    expect(offsetMs.value).toBe(50000)
    expect(wrapper.find('.gap-overlay').exists()).toBe(false)
    expect(wrapper.find('v-btn-stub[icon="mdi-pause"]').exists()).toBe(true)
    expect(loadWindow).toHaveBeenCalledExactlyOnceWith(50000, expect.objectContaining({ prefetch: false }))
    expect(hlsState.instances).toHaveLength(1)
    expect(element.currentTime).toBe(0)
    expect(HTMLMediaElement.prototype.play).toHaveBeenCalled()
  })

  it('首帧较请求起点偏移 21ms 时，重复定位零点复用已安装窗口', async () =>
  {
    const loadWindow = vi.fn<(offsetMs: number, options?: PlaybackLoadOptions) => Promise<PlaybackWindow | undefined>>()
      .mockResolvedValue(playback(21, 30000))
    const state = setupPlayer(loadWindow)
    await flushPromises()
    ready(state)
    await state.controller.seek(0)
    expect(loadWindow).toHaveBeenCalledTimes(1)
    expect(hlsState.instances).toHaveLength(1)
    expect(state.currentMs.value).toBe(0)
    expect(state.video.value!.currentTime).toBe(0)
  })

  it('窗口内只定位当前媒体，停止加载后才恢复 HLS，保留暂停与音量倍速', async () =>
  {
    const state = setupPlayer()
    await flushPromises()
    ready(state)
    state.controller.rate.value = 1.5
    state.controller.volume.value = 0.4
    await state.controller.seek(12000)
    expect(state.loadWindow).toHaveBeenCalledTimes(1)
    expect(hlsState.instances).toHaveLength(1)
    expect(hlsState.instances[0]!.startLoad).not.toHaveBeenCalled()
    expect(state.video.value!.currentTime).toBe(12)
    expect(state.video.value!.playbackRate).toBe(1.5)
    expect(state.video.value!.volume).toBe(0.4)
    expect(state.controller.playing.value).toBe(false)
    expect(HTMLMediaElement.prototype.play).not.toHaveBeenCalled()
    state.gaps.value = [{ startMs: 40000, endMs: 50000 }]
    await state.controller.seek(45000)
    await state.controller.seek(15000)
    expect(hlsState.instances[0]!.startLoad).toHaveBeenCalledExactlyOnceWith(15)
  })

  it('连续跨窗取消旧请求，迟到结果和旧源错误不能覆盖新目标', async () =>
  {
    const old = deferred<PlaybackWindow | undefined>()
    const state = setupPlayer()
    await flushPromises()
    ready(state)
    const oldSource = hlsState.instances[0]!
    state.loadWindow.mockReturnValueOnce(old.promise).mockResolvedValueOnce(playback(60000))
    const first = state.controller.seek(35000)
    const signal = state.loadWindow.mock.calls[1]![1]!.signal!
    await state.controller.seek(65000)
    expect(signal.aborted).toBe(true)
    ready(state)
    old.resolve(playback(30000))
    await first
    oldSource.emit('error', { fatal: true })
    expect(state.currentMs.value).toBe(65000)
    expect(state.video.value!.currentTime).toBe(5)
    expect(hlsState.instances).toHaveLength(2)
    expect(state.controller.error.value).toBe('')
  })

  it('相同待处理目标共享请求，返回已安装窗口则取消跨窗工作', async () =>
  {
    const pending = deferred<PlaybackWindow | undefined>()
    const state = setupPlayer()
    await flushPromises()
    ready(state)
    state.loadWindow.mockReturnValueOnce(pending.promise)
    const first = state.controller.seek(35000)
    const second = state.controller.seek(35000)
    expect(state.loadWindow).toHaveBeenCalledTimes(2)
    await state.controller.seek(10000)
    expect(state.loadWindow.mock.calls[1]![1]!.signal!.aborted).toBe(true)
    pending.resolve(playback(30000))
    await Promise.all([first, second])
    expect(state.video.value!.currentTime).toBe(10)
    expect(hlsState.instances).toHaveLength(1)
  })

  it('新导航取消 425 重试等待，前台空响应作为错误', async () =>
  {
    const state = setupPlayer()
    await flushPromises()
    ready(state)
    state.loadWindow.mockRejectedValueOnce(new ApiError(425, '尚未就绪'))
    const waiting = state.controller.seek(35000)
    await flushPromises()
    await state.controller.seek(10000)
    await vi.advanceTimersByTimeAsync(1200)
    await waiting
    expect(state.loadWindow).toHaveBeenCalledTimes(2)
    state.loadWindow.mockResolvedValueOnce(undefined)
    await state.controller.seek(65000)
    expect(state.controller.error.value).not.toBe('')
    expect(state.controller.loading.value).toBe(false)
  })

  it('暂停状态的原生 HLS 跳转显示目标帧，卸载后迟到响应不装载媒体', async () =>
  {
    hlsState.supported = false
    const state = setupPlayer()
    await flushPromises()
    ready(state)
    await state.controller.seek(5000)
    ready(state)
    expect(state.video.value!.currentTime).toBe(5)
    expect(state.controller.loading.value).toBe(false)
    expect(HTMLMediaElement.prototype.play).not.toHaveBeenCalled()
    const pending = deferred<PlaybackWindow | undefined>()
    state.loadWindow.mockReturnValueOnce(pending.promise)
    const request = state.controller.seek(35000)
    const loadCount = vi.mocked(HTMLMediaElement.prototype.load).mock.calls.length
    state.wrapper.unmount()
    expect(state.loadWindow.mock.calls[1]![1]!.signal!.aborted).toBe(true)
    pending.resolve(playback(30000))
    await request
    expect(vi.mocked(HTMLMediaElement.prototype.load).mock.calls.length).toBe(loadCount)
  })

  it('旧 play Promise 被拒绝不暂停新导航后的播放', async () =>
  {
    const previousPlay = deferred<void>()
    const state = setupPlayer()
    await flushPromises()
    ready(state)
    vi.mocked(HTMLMediaElement.prototype.play).mockReturnValueOnce(previousPlay.promise)
    await state.controller.togglePlay()
    state.loadWindow.mockResolvedValueOnce(playback(60000))
    await state.controller.seek(65000)
    ready(state)
    previousPlay.reject(new DOMException('The source changed', 'AbortError'))
    await flushPromises()
    expect(state.controller.playing.value).toBe(true)
    expect(state.controller.error.value).toBe('')
  })

  it('仍在 seeking 时媒体就绪事件不结束加载或回写旧进度', async () =>
  {
    const state = setupPlayer()
    await flushPromises()
    ready(state)
    await state.controller.togglePlay()
    await state.controller.seek(12000)
    Object.defineProperty(state.video.value!, 'seeking', { configurable: true, value: true })
    state.video.value!.currentTime = 2
    state.video.value!.dispatchEvent(new Event('canplay'))
    tick(1000)
    expect(state.controller.loading.value).toBe(true)
    expect(state.currentMs.value).toBe(12000)
    Object.defineProperty(state.video.value!, 'seeking', { configurable: true, value: false })
    state.video.value!.currentTime = 12
    state.video.value!.dispatchEvent(new Event('seeked'))
    expect(state.controller.loading.value).toBe(false)
  })
})

describe('有界下一窗口预取', () =>
{
  it('按墙钟剩余时间预热下一窗口首片，边界提升同一实例且只加载一次清单', async () =>
  {
    const state = setupPlayer()
    await flushPromises()
    state.loadWindow.mockResolvedValueOnce(playback(30000))
    ready(state)
    state.controller.rate.value = 2
    await state.controller.togglePlay()
    state.video.value!.currentTime = 11
    tick(1000)
    await flushPromises()
    expect(state.loadWindow).toHaveBeenLastCalledWith(30000, expect.objectContaining({ prefetch: true }))
    expect(hlsState.instances).toHaveLength(2)
    const prepared = hlsState.instances[1]!
    expect(prepared.config).toMatchObject({ startFragPrefetch: true, maxBufferLength: 1, maxMaxBufferLength: 1 })
    expect(prepared.attachMedia).not.toHaveBeenCalled()
    expect(prepared.loadSource).toHaveBeenCalledTimes(1)
    expect(hlsState.instances[0]!.destroy).not.toHaveBeenCalled()
    tick(1100)
    tick(1200)
    expect(state.loadWindow).toHaveBeenCalledTimes(2)
    state.video.value!.currentTime = 30
    tick(2000)
    await flushPromises()
    ready(state)
    expect(state.loadWindow).toHaveBeenCalledTimes(2)
    expect(state.video.value!.currentTime).toBe(0)
    expect(hlsState.instances).toHaveLength(2)
    expect(prepared.attachMedia).toHaveBeenCalledExactlyOnceWith(state.video.value)
    expect(prepared.loadSource).toHaveBeenCalledTimes(1)
    expect(prepared.config.maxBufferLength).toBe(30)
    expect(hlsState.instances[0]!.destroy).toHaveBeenCalledTimes(1)
  })

  it.each(['pause', 'hidden', 'navigation', 'quality', 'unmount'])('%s 销毁已预热首片，迟到错误不影响活动播放', async reason =>
  {
    const state = setupPlayer()
    await flushPromises()
    state.loadWindow.mockResolvedValueOnce(playback(30000))
    await nearEnd(state)
    const prepared = hlsState.instances[1]!
    if (reason === 'pause') { await state.controller.togglePlay() }
    else if (reason === 'hidden') { visibility = 'hidden'; document.dispatchEvent(new Event('visibilitychange')) }
    else if (reason === 'navigation') { await state.controller.seek(5000) }
    else if (reason === 'quality')
    {
      state.controller.qualities.value.push({ id: '720p', name: '720p', available: true, state: 'Ready' })
      state.loadWindow.mockResolvedValueOnce(playback())
      await state.controller.selectQuality('720p')
    }
    else { state.wrapper.unmount() }
    expect(prepared.destroy).toHaveBeenCalledTimes(1)
    prepared.emit('error', { fatal: true })
    expect(state.controller.error.value).toBe('')
    expect(prepared.attachMedia).not.toHaveBeenCalled()
  })

  it('预热阶段保存转码时基，在提升时定位首帧，旧就绪事件不会覆盖映射', async () =>
  {
    const state = setupPlayer()
    await flushPromises()
    state.loadWindow.mockResolvedValueOnce({ ...playback(30000 - 400 / 3, 60000), mediaStartOffsetMs: 30000, timestampMapping: 'HlsInitPts', requestedQualityId: '720p', effectiveQualityId: '720p' })
    await nearEnd(state)
    const prepared = hlsState.instances[1]!
    prepared.emit('initPts', { id: 'main', initPTS: 6400, timescale: 48000 })
    prepared.emit('initPts', { id: 'audio', initPTS: 0, timescale: 48000 })
    await state.controller.seek(30000)
    ready(state)
    expect(state.controller.loading.value).toBe(false)
    expect(state.video.value!.currentTime).toBeCloseTo(0)
    expect(hlsState.instances).toHaveLength(2)
  })

  it('待用源失败静默释放，边界通过前台重新获取', async () =>
  {
    const state = setupPlayer()
    await flushPromises()
    state.loadWindow.mockResolvedValueOnce(playback(30000))
    await nearEnd(state)
    const prepared = hlsState.instances[1]!
    prepared.emit('error', { fatal: true })
    expect(prepared.destroy).toHaveBeenCalledTimes(1)
    expect(state.controller.error.value).toBe('')
    expect(state.controller.playing.value).toBe(true)
    tick(1500)
    expect(state.loadWindow).toHaveBeenCalledTimes(2)
    state.loadWindow.mockResolvedValueOnce(playback(30000))
    await state.controller.seek(30000)
    ready(state)
    expect(state.loadWindow).toHaveBeenLastCalledWith(30000, expect.objectContaining({ prefetch: false }))
  })

  it.each(['pause', 'hidden'])('%s 结束后复用窗口地址重新预热，暂停期间不创建媒体加载', async reason =>
  {
    const state = setupPlayer()
    await flushPromises()
    state.loadWindow.mockResolvedValueOnce(playback(30000))
    await nearEnd(state)
    if (reason === 'pause') { await state.controller.togglePlay() }
    else { visibility = 'hidden'; document.dispatchEvent(new Event('visibilitychange')) }
    tick(1500)
    expect(hlsState.instances).toHaveLength(2)
    if (reason === 'pause') { await state.controller.togglePlay() }
    else { visibility = 'visible'; document.dispatchEvent(new Event('visibilitychange')) }
    tick(2000)
    expect(state.loadWindow).toHaveBeenCalledTimes(2)
    expect(hlsState.instances).toHaveLength(3)
    expect(hlsState.instances[2]!.attachMedia).not.toHaveBeenCalled()
  })

  it('同窗口导航销毁待用源，再接近边界时复用地址重新预热', async () =>
  {
    const state = setupPlayer()
    await flushPromises()
    state.loadWindow.mockResolvedValueOnce(playback(30000))
    await nearEnd(state)
    await state.controller.seek(5000)
    ready(state)
    expect(hlsState.instances[1]!.destroy).toHaveBeenCalledTimes(1)
    state.video.value!.currentTime = 21
    tick(2000)
    expect(state.loadWindow).toHaveBeenCalledTimes(2)
    expect(hlsState.instances).toHaveLength(3)
    expect(hlsState.instances[2]!.attachMedia).not.toHaveBeenCalled()
  })

  it('长录像在真实分段末端提升未完成的预取，并接入后续短窗口', async () =>
  {
    const boundaryMs = 2 * 60 * 60 * 1000
    const pending = deferred<PlaybackWindow | undefined>()
    const loadWindow = vi.fn<(offsetMs: number, options?: PlaybackLoadOptions) => Promise<PlaybackWindow | undefined>>()
      .mockResolvedValue(playback(0, boundaryMs))
    const state = setupPlayer(loadWindow, { durationMs: boundaryMs + 120000 })
    await flushPromises()
    state.loadWindow.mockReturnValueOnce(pending.promise)
    await nearEnd(state, boundaryMs / 1000 - 9)
    const signal = state.loadWindow.mock.calls[1]![1]!.signal!
    expect(state.loadWindow).toHaveBeenLastCalledWith(boundaryMs, expect.objectContaining({ prefetch: true }))
    state.video.value!.currentTime = boundaryMs / 1000
    tick(2000)
    expect(signal.aborted).toBe(false)
    expect(state.loadWindow).toHaveBeenCalledTimes(2)
    pending.resolve(playback(boundaryMs))
    await flushPromises()
    ready(state)
    expect(state.currentMs.value).toBe(boundaryMs)
    expect(state.video.value!.currentTime).toBe(0)
    expect(state.controller.playing.value).toBe(true)
    expect(hlsState.instances).toHaveLength(2)
  })

  it('已提升的预取返回 204 时立即改用前台请求', async () =>
  {
    const pending = deferred<PlaybackWindow | undefined>()
    const state = setupPlayer()
    await flushPromises()
    state.loadWindow.mockReturnValueOnce(pending.promise).mockResolvedValueOnce(playback(30000))
    await nearEnd(state)
    const navigation = state.controller.seek(30000)
    pending.resolve(undefined)
    await navigation
    expect(state.loadWindow).toHaveBeenCalledTimes(3)
    expect(state.loadWindow).toHaveBeenLastCalledWith(30000, expect.objectContaining({ prefetch: false }))
    expect(state.controller.error.value).toBe('')
    ready(state)
    expect(state.controller.loading.value).toBe(false)
  })

  it('就绪预取与当前源重叠时保留当前源，并按真实结束时间预取', async () =>
  {
    const loadWindow = vi.fn<(offsetMs: number, options?: PlaybackLoadOptions) => Promise<PlaybackWindow | undefined>>()
      .mockResolvedValueOnce(playback(0, 32000)).mockResolvedValueOnce(playback(28000, 62000))
    const state = setupPlayer(loadWindow)
    await flushPromises()
    await nearEnd(state, 25)
    expect(loadWindow).toHaveBeenLastCalledWith(32000, expect.objectContaining({ prefetch: true }))
    await state.controller.seek(30000)
    ready(state)
    expect(hlsState.instances).toHaveLength(2)
    expect(hlsState.instances[0]!.destroy).not.toHaveBeenCalled()
    expect(hlsState.instances[1]!.destroy).toHaveBeenCalledTimes(1)
    await state.controller.seek(40000)
    ready(state)
    expect(loadWindow).toHaveBeenCalledTimes(2)
    expect(hlsState.instances).toHaveLength(3)
    expect(state.video.value!.currentTime).toBe(12)
  })

  it('不同新目标取消未完成预取，迟到结果不取代前台窗口', async () =>
  {
    const pending = deferred<PlaybackWindow | undefined>()
    const state = setupPlayer()
    await flushPromises()
    state.loadWindow.mockReturnValueOnce(pending.promise).mockResolvedValueOnce(playback(60000))
    await nearEnd(state)
    const signal = state.loadWindow.mock.calls[1]![1]!.signal!
    await state.controller.seek(65000)
    expect(signal.aborted).toBe(true)
    pending.resolve(playback(30000))
    await flushPromises()
    ready(state)
    expect(state.currentMs.value).toBe(65000)
    expect(state.video.value!.currentTime).toBe(5)
    expect(hlsState.instances).toHaveLength(2)
  })

  it.each(['skip', 'error'])('预取 %s 不报播放错误、不循环重试，续播转为前台请求', async result =>
  {
    const state = setupPlayer()
    await flushPromises()
    if (result === 'skip') { state.loadWindow.mockResolvedValueOnce(undefined) }
    else { state.loadWindow.mockRejectedValueOnce(new Error('预取失败')) }
    await nearEnd(state)
    tick(2000)
    await vi.advanceTimersByTimeAsync(5000)
    expect(state.loadWindow).toHaveBeenCalledTimes(2)
    expect(state.controller.error.value).toBe('')
    state.loadWindow.mockResolvedValueOnce(playback(30000))
    await state.controller.seek(30000)
    expect(state.loadWindow).toHaveBeenLastCalledWith(30000, expect.objectContaining({ prefetch: false }))
  })

  it.each(['pause', 'hidden'])('%s 取消后台预取，恢复后允许重试，前台跳转不被取消', async reason =>
  {
    const pending = deferred<PlaybackWindow | undefined>()
    const state = setupPlayer()
    await flushPromises()
    state.loadWindow.mockReturnValueOnce(pending.promise)
    await nearEnd(state)
    const signal = state.loadWindow.mock.calls[1]![1]!.signal!
    if (reason === 'pause') { await state.controller.togglePlay() }
    else { visibility = 'hidden'; document.dispatchEvent(new Event('visibilitychange')) }
    expect(signal.aborted).toBe(true)
    state.loadWindow.mockResolvedValueOnce(playback(30000))
    if (reason === 'pause') { await state.controller.togglePlay() }
    else { visibility = 'visible'; document.dispatchEvent(new Event('visibilitychange')) }
    tick(2000)
    await flushPromises()
    expect(state.loadWindow).toHaveBeenCalledTimes(3)
    const foreground = deferred<PlaybackWindow | undefined>()
    state.loadWindow.mockReturnValueOnce(foreground.promise)
    const navigation = state.controller.seek(65000)
    const foregroundSignal = state.loadWindow.mock.calls[3]![1]!.signal!
    visibility = 'hidden'
    document.dispatchEvent(new Event('visibilitychange'))
    expect(foregroundSignal.aborted).toBe(false)
    foreground.resolve(playback(60000))
    pending.resolve(playback(30000))
    await navigation
  })

  it('拖动、真实空档和录像末尾不触发下一窗预取', async () =>
  {
    const state = setupPlayer()
    await flushPromises()
    state.scrubbing.value = true
    await nearEnd(state)
    expect(state.loadWindow).toHaveBeenCalledTimes(1)
    state.scrubbing.value = false
    state.gaps.value = [{ startMs: 30000, endMs: 45000 }]
    tick(2000)
    expect(state.loadWindow).toHaveBeenCalledTimes(1)
    state.gaps.value = []
    state.durationMs.value = 30000
    tick(3000)
    expect(state.loadWindow).toHaveBeenCalledTimes(1)
  })

  it('预取源失效只经前台重取一次，旧错误不能触发重复恢复', async () =>
  {
    const state = setupPlayer()
    await flushPromises()
    state.loadWindow.mockResolvedValueOnce(playback(30000))
    await nearEnd(state)
    await state.controller.seek(35000)
    ready(state)
    const prefetchedSource = hlsState.instances.at(-1)!
    state.loadWindow.mockResolvedValueOnce(playback(30000))
    prefetchedSource.emit('error', { fatal: true })
    await flushPromises()
    prefetchedSource.emit('error', { fatal: true })
    expect(state.loadWindow).toHaveBeenCalledTimes(3)
    expect(state.loadWindow).toHaveBeenLastCalledWith(35000, expect.objectContaining({ prefetch: false }))
    hlsState.instances.at(-1)!.emit('error', { fatal: true })
    expect(state.controller.error.value).not.toBe('')
    expect(state.loadWindow).toHaveBeenCalledTimes(3)
  })

  it('原生 HLS 的过期预取源也能经前台重取一次', async () =>
  {
    hlsState.supported = false
    const state = setupPlayer()
    await flushPromises()
    state.loadWindow.mockResolvedValueOnce(playback(30000))
    await nearEnd(state)
    await state.controller.seek(35000)
    state.loadWindow.mockResolvedValueOnce(playback(30000))
    Object.defineProperty(state.video.value!, 'error', { configurable: true, value: { code: 2 } })
    state.video.value!.dispatchEvent(new Event('error'))
    await flushPromises()
    expect(state.loadWindow).toHaveBeenCalledTimes(3)
    expect(state.loadWindow).toHaveBeenLastCalledWith(35000, expect.objectContaining({ prefetch: false }))
    Object.defineProperty(state.video.value!, 'error', { configurable: true, value: null })
    ready(state)
    expect(state.controller.error.value).toBe('')
  })
})

describe('转码时间戳映射', () =>
{
  function mappedPlayback(baseStart = 10000 - 400 / 3): PlaybackWindow
  {
    return { ...playback(baseStart, 60000), mediaStartOffsetMs: 10000, timestampMapping: 'HlsInitPts', requestedQualityId: '720p', effectiveQualityId: '720p' }
  }
  function initPts(instance: HlsDouble, milliseconds: number, timescale = 48000): void
  {
    instance.emit('initPts', { id: 'main', initPTS: milliseconds / 1000 * timescale, timescale })
  }

  it.each(['beforeMetadata', 'afterMetadata'])('%s 冷启动使用有符号initPTS修正目标，元数据本身不会提前完成转码预览', async order =>
  {
    const state = setupPlayer(vi.fn().mockResolvedValue(mappedPlayback()), { initialOffsetMs: 19000 })
    await flushPromises()
    const instance = hlsState.instances[0]!
    if (order === 'afterMetadata')
    {
      ready(state)
      expect(state.controller.loading.value).toBe(true)
      expect(instance.pauseBuffering).not.toHaveBeenCalled()
    }
    initPts(instance, -112)
    ready(state)
    expect(state.video.value!.currentTime).toBeCloseTo((19000 - (10000 - 400 / 3 - 112)) / 1000, 8)
    expect(state.currentMs.value).toBe(19000)
    expect(state.controller.loading.value).toBe(false)
    expect(instance.pauseBuffering).toHaveBeenCalled()
    expect(state.loadWindow).toHaveBeenCalledTimes(1)
    expect(instance.destroy).not.toHaveBeenCalled()
  })

  it('同源后续seek与播放时钟使用有效偏移，等值事件不把播放拉回初始位置', async () =>
  {
    const state = setupPlayer(vi.fn().mockResolvedValue(mappedPlayback()), { initialOffsetMs: 19000 })
    await flushPromises()
    const instance = hlsState.instances[0]!
    initPts(instance, 125)
    ready(state)
    await state.controller.seek(22000)
    ready(state)
    const expected = (22000 - (10000 - 400 / 3 + 125)) / 1000
    expect(state.video.value!.currentTime).toBeCloseTo(expected, 8)
    await state.controller.togglePlay()
    state.video.value!.currentTime = expected + 1
    initPts(instance, 125, 90000)
    expect(state.video.value!.currentTime).toBeCloseTo(expected + 1, 8)
    tick(1000)
    expect(state.currentMs.value).toBeCloseTo(23000, 6)
    expect(state.loadWindow).toHaveBeenCalledTimes(1)
    expect(hlsState.instances).toHaveLength(1)
  })

  it('同源seek切换generation的同一事件中收到首次initPTS时不会丢失映射', async () =>
  {
    const state = setupPlayer(vi.fn().mockResolvedValue(mappedPlayback()), { initialOffsetMs: 19000 })
    await flushPromises()
    const navigation = state.controller.seek(22000)
    initPts(hlsState.instances[0]!, -112)
    await navigation
    ready(state)
    expect(state.video.value!.currentTime).toBeCloseTo((22000 - (10000 - 400 / 3 - 112)) / 1000, 8)
    expect(state.currentMs.value).toBe(22000)
    expect(state.controller.loading.value).toBe(false)
    expect(state.loadWindow).toHaveBeenCalledTimes(1)
  })

  it('播放中映射变化保留当前全场位置，待定位时则保留最新seek目标', async () =>
  {
    const state = setupPlayer(vi.fn().mockResolvedValue(mappedPlayback(10000)), { initialOffsetMs: 19000 })
    await flushPromises()
    const instance = hlsState.instances[0]!
    initPts(instance, 0)
    ready(state)
    await state.controller.togglePlay()
    state.video.value!.currentTime = 15
    initPts(instance, 200)
    expect(state.video.value!.currentTime).toBeCloseTo(14.8, 8)
    ready(state)
    tick(1000)
    expect(state.currentMs.value).toBeCloseTo(25000, 6)
    await state.controller.seek(27000)
    initPts(instance, -100)
    expect(state.video.value!.currentTime).toBeCloseTo(17.1, 8)
    ready(state)
    expect(state.currentMs.value).toBe(27000)
    expect(state.loadWindow).toHaveBeenCalledTimes(1)
  })

  it('映射基准不会扩大物理段范围，旧源事件无法修改新源', async () =>
  {
    const loadWindow = vi.fn<(offsetMs: number, options?: PlaybackLoadOptions) => Promise<PlaybackWindow | undefined>>()
      .mockResolvedValueOnce(mappedPlayback(9000)).mockResolvedValueOnce(playback(0, 10000))
    const state = setupPlayer(loadWindow, { initialOffsetMs: 19000 })
    await flushPromises()
    const old = hlsState.instances[0]!
    initPts(old, -112)
    ready(state)
    await state.controller.seek(9500)
    ready(state)
    expect(loadWindow).toHaveBeenCalledTimes(2)
    expect(state.video.value!.currentTime).toBe(9.5)
    initPts(old, 5000)
    expect(state.video.value!.currentTime).toBe(9.5)
    expect(state.controller.loading.value).toBe(false)
  })

  it('忽略非main或非有限的时基，原画不应用转码事件', async () =>
  {
    const state = setupPlayer(vi.fn().mockResolvedValue(mappedPlayback()), { initialOffsetMs: 19000 })
    await flushPromises()
    const instance = hlsState.instances[0]!
    ready(state)
    for (const data of [{ id: 'audio', initPTS: 1, timescale: 1 }, { id: 'main', initPTS: Number.NaN, timescale: 1 }, { id: 'main', initPTS: 1, timescale: 0 }])
    {
      instance.emit('initPts', data)
    }
    expect(state.controller.loading.value).toBe(true)
    initPts(instance, 0)
    ready(state)
    expect(state.controller.loading.value).toBe(false)
    const original = setupPlayer()
    await flushPromises()
    ready(original)
    initPts(hlsState.instances[1]!, 5000)
    expect(original.video.value!.currentTime).toBe(0)
    expect(original.controller.loading.value).toBe(false)
  })

  it('原生HLS保留服务端基准，无需等待仅hls.js提供的事件', async () =>
  {
    hlsState.supported = false
    const state = setupPlayer(vi.fn().mockResolvedValue(mappedPlayback()), { initialOffsetMs: 19000 })
    await flushPromises()
    ready(state)
    expect(state.video.value!.currentTime).toBeCloseTo((19000 - (10000 - 400 / 3)) / 1000, 8)
    expect(state.controller.loading.value).toBe(false)
    expect(state.loadWindow).toHaveBeenCalledTimes(1)
  })

  it('动态PTS与较短媒体尾部同时出现时完成预览，后续时基变化不覆盖全场目标显示', async () =>
  {
    const window = { ...mappedPlayback(-400 / 3), mediaStartOffsetMs: 0, endOffsetMs: 95288, durationMs: 95288 }
    const state = setupPlayer(vi.fn().mockResolvedValue(window), { durationMs: 95288, initialOffsetMs: 95287 })
    await flushPromises()
    const instance = hlsState.instances[0]!
    Object.defineProperty(state.video.value!, 'duration', { configurable: true, value: 95 })
    initPts(instance, -112)
    ready(state)
    expect(state.video.value!.currentTime).toBeCloseTo(94.999, 6)
    expect(state.controller.loading.value).toBe(false)
    expect(state.currentMs.value).toBe(95287)
    initPts(instance, 0)
    ready(state)
    expect(state.video.value!.currentTime).toBeCloseTo(94.999, 6)
    expect(state.controller.loading.value).toBe(false)
    expect(state.currentMs.value).toBe(95287)
    expect(state.loadWindow).toHaveBeenCalledTimes(1)
  })

  it.each([
    { mode: 'hlsjs', offset: -112, duration: 95.14, expected: 95.111 },
    { mode: 'hlsjs', offset: 125, duration: 95.14, expected: 94.874 },
    { mode: 'hlsjs', offset: -112, duration: 94, expected: 93.999 },
    { mode: 'native', offset: 0, duration: 95.14, expected: 94.999 },
  ])('$mode 音轨尾长于视频时按视频结束时间和 $offset ms 时基定位末帧', async ({ mode, offset, duration, expected }) =>
  {
    hlsState.supported = mode === 'hlsjs'
    const window = { ...mappedPlayback(-400 / 3), mediaStartOffsetMs: 0, mediaEndTimestampMs: 95000, endOffsetMs: 95288, durationMs: 95288 }
    const state = setupPlayer(vi.fn().mockResolvedValue(window), { durationMs: 95288, initialOffsetMs: 95287 })
    await flushPromises()
    Object.defineProperty(state.video.value!, 'duration', { configurable: true, value: duration })
    if (mode === 'hlsjs') { initPts(hlsState.instances[0]!, offset) }
    ready(state)
    expect(state.video.value!.currentTime).toBeCloseTo(expected, 6)
    expect(state.controller.loading.value).toBe(false)
    expect(state.currentMs.value).toBe(95287)
    state.video.value!.dispatchEvent(new Event('durationchange'))
    expect(state.video.value!.currentTime).toBeCloseTo(expected, 6)
    expect(state.controller.loading.value).toBe(false)
    expect(state.loadWindow).toHaveBeenCalledTimes(1)
  })

  it('原画缺少转码映射标记时不使用转码视频终点字段', async () =>
  {
    const state = setupPlayer(vi.fn().mockResolvedValue({ ...playback(0, 95288), mediaEndTimestampMs: 10000 }), { durationMs: 95288, initialOffsetMs: 95287 })
    await flushPromises()
    Object.defineProperty(state.video.value!, 'duration', { configurable: true, value: 95.14 })
    ready(state)
    expect(state.video.value!.currentTime).toBeCloseTo(95.139, 6)
    expect(state.controller.loading.value).toBe(false)
  })
})

describe('手动播放清晰度', () =>
{
  function qualityPlayback(quality = '720p', start = 0, effective = quality): PlaybackWindow
  {
    return { ...playback(start), url: `/api/${effective}/${start}/index.m3u8`, requestedQualityId: quality, effectiveQualityId: effective, artifactVersion: `${effective}-v1`, fallbackReason: effective === quality ? undefined : 'Pending', qualities: [
      { id: 'original', name: '原画', available: true, state: 'Ready' }, { id: '720p', name: '720p', height: 720, available: true, state: 'Ready' }, { id: '1080p', name: '1080p', height: 1080, available: false, state: 'Pending' },
    ] }
  }
  function qualityLoader()
  {
    return vi.fn<(offset: number, options?: PlaybackLoadOptions) => Promise<PlaybackWindow | undefined>>()
      .mockImplementation(async (offset, options) => qualityPlayback(options?.qualityId ?? '720p', Math.floor(offset / 30000) * 30000))
  }

  it.each(['hlsjs', 'native'])('%s 切档保留位置、播放意图、倍速和音量；同档跳转复用', async mode =>
  {
    hlsState.supported = mode === 'hlsjs'
    const state = setupPlayer(qualityLoader())
    await flushPromises()
    ready(state)
    expect(state.controller.requestedQualityId.value).toBe('720p')
    state.controller.rate.value = 1.5
    state.controller.volume.value = .4
    await state.controller.seek(12000)
    ready(state)
    expect(state.loadWindow).toHaveBeenCalledTimes(1)
    await state.controller.togglePlay()
    await state.controller.selectQuality('original')
    ready(state)
    expect(state.loadWindow).toHaveBeenLastCalledWith(12000, expect.objectContaining({ qualityId: 'original', prefetch: false }))
    expect(state.controller.playing.value).toBe(true)
    expect(state.video.value!.currentTime).toBe(12)
    expect(state.video.value!.playbackRate).toBe(1.5)
    expect(state.video.value!.volume).toBe(.4)
    expect(state.controller.effectiveQualityId.value).toBe('original')
    const count = state.loadWindow.mock.calls.length
    await state.controller.selectQuality('original')
    await state.controller.seek(15000)
    expect(state.loadWindow).toHaveBeenCalledTimes(count)
    expect(hlsState.instances).toHaveLength(mode === 'hlsjs' ? 2 : 0)
  })

  it('快速切档只安装最后请求，过期回包不会恢复旧档位', async () =>
  {
    const older = deferred<PlaybackWindow>()
    const newer = deferred<PlaybackWindow>()
    const loadWindow = qualityLoader().mockResolvedValueOnce(qualityPlayback()).mockReturnValueOnce(older.promise).mockReturnValueOnce(newer.promise)
    const state = setupPlayer(loadWindow)
    await flushPromises()
    ready(state)
    const first = state.controller.selectQuality('original')
    await flushPromises()
    const oldSignal = loadWindow.mock.calls[1]![1]!.signal!
    const second = state.controller.selectQuality('1080p')
    newer.resolve(qualityPlayback('1080p', 0, 'original'))
    await second
    older.resolve(qualityPlayback('original'))
    await first
    expect(oldSignal.aborted).toBe(true)
    expect(state.controller.requestedQualityId.value).toBe('1080p')
    expect(state.controller.effectiveQualityId.value).toBe('original')
    expect(state.controller.fallbackReason.value).toBe('Pending')
    expect(hlsState.instances).toHaveLength(2)
    expect(state.controller.playing.value).toBe(false)
  })

  it('转码资源失败回原画一次，保留请求档位并在下一物理段重新尝试', async () =>
  {
    const state = setupPlayer(qualityLoader())
    await flushPromises()
    ready(state)
    await state.controller.seek(12000)
    ready(state)
    hlsState.instances[0]!.emit('error', { fatal: true, response: { code: 503 } })
    await flushPromises()
    ready(state)
    expect(state.loadWindow).toHaveBeenLastCalledWith(12000, expect.objectContaining({ qualityId: 'original' }))
    expect(state.controller.requestedQualityId.value).toBe('720p')
    expect(state.controller.effectiveQualityId.value).toBe('original')
    expect(state.controller.fallbackReason.value).toBe('Unavailable')
    await state.controller.seek(35000)
    expect(state.loadWindow).toHaveBeenLastCalledWith(35000, expect.objectContaining({ qualityId: '720p' }))
    hlsState.instances.at(-1)!.emit('error', { fatal: true, response: { code: 404 } })
    await flushPromises()
    const count = state.loadWindow.mock.calls.length
    hlsState.instances.at(-1)!.emit('error', { fatal: true, response: { code: 404 } })
    await flushPromises()
    expect(state.loadWindow).toHaveBeenCalledTimes(count)
    expect(state.controller.error.value).not.toBe('')
  })

  it.each([401, 403])('转码媒体响应 %s 不触发原画重试', async status =>
  {
    const state = setupPlayer(qualityLoader())
    await flushPromises()
    ready(state)
    hlsState.instances[0]!.emit('error', { fatal: true, response: { code: status } })
    await flushPromises()
    expect(state.loadWindow).toHaveBeenCalledTimes(1)
    expect(state.controller.error.value).not.toBe('')
  })

  it.each([401, 403])('选档接口响应 %s 不绕过授权请求原画', async status =>
  {
    const state = setupPlayer(qualityLoader().mockResolvedValueOnce(qualityPlayback()).mockRejectedValueOnce(new ApiError(status, 'denied')))
    await flushPromises()
    ready(state)
    await state.controller.selectQuality('1080p')
    expect(state.loadWindow).toHaveBeenCalledTimes(2)
    expect(state.controller.error.value).toBe('denied')
  })

  it('选档失败后原画失败不会递归；重试仍请求用户选择的清晰度', async () =>
  {
    const state = setupPlayer(qualityLoader().mockResolvedValueOnce(qualityPlayback()).mockRejectedValueOnce(new ApiError(500, 'encode failed')).mockRejectedValueOnce(new ApiError(500, 'original failed')))
    await flushPromises()
    ready(state)
    await state.controller.selectQuality('1080p')
    expect(state.loadWindow).toHaveBeenCalledTimes(3)
    expect(state.loadWindow).toHaveBeenLastCalledWith(0, expect.objectContaining({ qualityId: 'original' }))
    expect(state.controller.error.value).toBe('original failed')
    await state.controller.retry()
    expect(state.loadWindow).toHaveBeenLastCalledWith(0, expect.objectContaining({ qualityId: '1080p' }))
    expect(state.controller.error.value).toBe('')
  })

  it('切档丢弃已准备好的旧档预取，跨段请求新档位', async () =>
  {
    const state = setupPlayer(qualityLoader())
    await flushPromises()
    await nearEnd(state)
    expect(state.loadWindow).toHaveBeenLastCalledWith(30000, expect.objectContaining({ prefetch: true, qualityId: '720p' }))
    await state.controller.selectQuality('original')
    ready(state)
    await state.controller.seek(35000)
    expect(state.loadWindow).toHaveBeenLastCalledWith(35000, expect.objectContaining({ prefetch: false, qualityId: 'original' }))
    expect(hlsState.instances.at(-1)!.loadSource).toHaveBeenCalledWith(expect.stringContaining('/original/30000/'))
  })
})
