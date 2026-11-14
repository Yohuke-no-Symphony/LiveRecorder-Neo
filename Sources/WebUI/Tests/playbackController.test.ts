// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { defineComponent, h, ref } from 'vue'
import { ApiError } from '../src/apis/APIBase'
import { usePlaybackController } from '../src/composables/usePlaybackController'
import MediaPlayer from '../src/components/player/MediaPlayer.vue'
import type { PlaybackLoadOptions } from '../src/models/playbackLoadOptions'
import type { PlaybackWindow, TimelineGap } from '../src/models/media'
import { i18n } from '../src/plugins/i18n'

interface HlsDouble
{
  loadingEnabled: boolean
  startLoad: ReturnType<typeof vi.fn>
  stopLoad: ReturnType<typeof vi.fn>
  destroy: ReturnType<typeof vi.fn>
  loadSource: ReturnType<typeof vi.fn>
  emit: (event: string, data?: unknown) => void
}

const hlsState = vi.hoisted(() => ({ supported: true, instances: [] as HlsDouble[] }))
vi.mock('hls.js', () => ({ default: class
{
  static isSupported() { return hlsState.supported }
  static Events = { MANIFEST_PARSED: 'manifest', ERROR: 'error' }
  loadingEnabled = true
  listeners = new Map<string, (event: string, data?: unknown) => void>()
  startLoad = vi.fn(() => { this.loadingEnabled = true })
  stopLoad = vi.fn(() => { this.loadingEnabled = false })
  destroy = vi.fn()
  attachMedia = vi.fn()
  loadSource = vi.fn()
  constructor() { hlsState.instances.push(this) }
  on(event: string, callback: (event: string, data?: unknown) => void) { this.listeners.set(event, callback) }
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

function setupPlayer(loadWindow = vi.fn<(offsetMs: number, options?: PlaybackLoadOptions) => Promise<PlaybackWindow | undefined>>().mockResolvedValue(playback()))
{
  const video = ref<HTMLVideoElement>()
  const currentMs = ref(0)
  const durationMs = ref(120000)
  const gaps = ref<TimelineGap[]>([])
  const scrubbing = ref(false)
  let controller!: ReturnType<typeof usePlaybackController>
  const wrapper = mount(defineComponent({ setup()
  {
    controller = usePlaybackController({ video, currentMs, scrubbing, durationMs: () => durationMs.value, gaps: () => gaps.value, isLive: () => false, initialOffsetMs: () => 0, loadWindow })
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

describe('播放源定位与请求取消', () =>
{
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
  it('按墙钟剩余时间预取真实边界，只生成服务端窗口且只尝试一次', async () =>
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
    expect(hlsState.instances).toHaveLength(1)
    tick(1100)
    tick(1200)
    expect(state.loadWindow).toHaveBeenCalledTimes(2)
    await state.controller.seek(45000)
    ready(state)
    expect(state.loadWindow).toHaveBeenCalledTimes(2)
    expect(state.video.value!.currentTime).toBe(15)
    expect(hlsState.instances).toHaveLength(2)
  })

  it('自动续播提升边界上尚未完成的预取，不重复请求', async () =>
  {
    const pending = deferred<PlaybackWindow | undefined>()
    const state = setupPlayer()
    await flushPromises()
    state.loadWindow.mockReturnValueOnce(pending.promise)
    await nearEnd(state)
    const signal = state.loadWindow.mock.calls[1]![1]!.signal!
    state.video.value!.currentTime = 30
    tick(2000)
    expect(signal.aborted).toBe(false)
    expect(state.loadWindow).toHaveBeenCalledTimes(2)
    pending.resolve(playback(30000))
    await flushPromises()
    ready(state)
    expect(state.currentMs.value).toBe(30000)
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
    expect(hlsState.instances).toHaveLength(1)
    await state.controller.seek(40000)
    ready(state)
    expect(loadWindow).toHaveBeenCalledTimes(2)
    expect(hlsState.instances).toHaveLength(2)
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
    const prefetchedSource = hlsState.instances[1]!
    state.loadWindow.mockResolvedValueOnce(playback(30000))
    prefetchedSource.emit('error', { fatal: true })
    await flushPromises()
    prefetchedSource.emit('error', { fatal: true })
    expect(state.loadWindow).toHaveBeenCalledTimes(3)
    expect(state.loadWindow).toHaveBeenLastCalledWith(35000, expect.objectContaining({ prefetch: false }))
    hlsState.instances[2]!.emit('error', { fatal: true })
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
