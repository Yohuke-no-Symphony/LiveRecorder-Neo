import Hls, { type InitPTSFoundData } from 'hls.js'
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch, type Ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { ApiError } from '@/apis/APIBase'
import { getServerUrl } from '@/apis/ServerURL'
import type { PlaybackWindow, TimelineGap } from '@/models/media'
import { ORIGINAL_QUALITY_ID, type PlaybackQuality } from '@/models/transcoding'
import type { PlaybackLoadOptions } from '@/models/playbackLoadOptions'
import { clampOffset, findGap, normalizeTimelineGaps, playbackPosition, windowPosition } from '@/utilities/timeline'

const PREFETCH_LEAD_MS = 10000
const RETRY_DELAY_MS = 1000
const WINDOW_START_TOLERANCE_MS = 100
const PREVIEW_BUFFER_SECONDS = 1
const BUFFER_TARGET_SECONDS = 30
const BUFFER_LIMIT_SECONDS = 60

export function usePlaybackController(options: {
  video: Ref<HTMLVideoElement | undefined>
  currentMs: Ref<number>
  scrubbing: Ref<boolean>
  durationMs: () => number
  gaps: () => TimelineGap[]
  isLive: () => boolean
  initialOffsetMs: () => number
  loadWindow: (offsetMs: number, options?: PlaybackLoadOptions) => Promise<PlaybackWindow | undefined>
})
{
  const { t } = useI18n()
  const { video, currentMs, scrubbing } = options
  const playing = ref(false)
  const loading = ref(false)
  const error = ref('')
  const requestedQualityId = ref<string>()
  const effectiveQualityId = ref(ORIGINAL_QUALITY_ID)
  const qualities = ref<PlaybackQuality[]>([{ id: ORIGINAL_QUALITY_ID, name: '', available: true, state: 'Ready' }])
  const fallbackReason = ref('')
  const volume = ref(0.8)
  const muted = ref(false)
  const rate = ref(1)
  const serverGap = ref<TimelineGap>()
  const normalizedGaps = computed(() => normalizeTimelineGaps(options.gaps(), options.durationMs()))
  const gap = computed(() => findGap(currentMs.value, normalizedGaps.value) ?? (serverGap.value && currentMs.value >= serverGap.value.startMs && currentMs.value < serverGap.value.endMs ? serverGap.value : undefined))
  let source: {
    playback: PlaybackWindow
    qualityId: string
    operation: number
    hls: Hls | undefined
    startOffsetMs: number
    timestampReady: boolean
    needsPosition: boolean
    awaitingSeek: boolean
    previewPending: boolean
    fromPrefetch: boolean
    originalFallback: boolean
    cleanup: () => void
  } | undefined
  let foreground: ReturnType<typeof createRequest> | undefined
  let prefetch: ReturnType<typeof createRequest> | undefined
  let readyPrefetch: PlaybackWindow | undefined
  let readyPrefetchQualityId: string | undefined
  let warmedPrefetch: {
    playback: PlaybackWindow
    owner: object
    offsetMs: number
    hls: Hls
    initPts: InitPTSFoundData | undefined
    cleanup: () => void
  } | undefined
  let prefetchAttemptedFor: object | undefined
  let generation = 0
  let playAttempt = 0
  let pendingOffsetMs = 0
  let frame = 0
  let lastFrameAt = 0
  let disposed = false

  function isCurrent(operation: number): boolean
  {
    return !disposed && operation === generation
  }

  function activeSource(value: typeof source): value is NonNullable<typeof source>
  {
    return !!value && source === value && isCurrent(value.operation) && !gap.value
  }

  function contains(playback: PlaybackWindow | undefined, offsetMs: number): boolean
  {
    return !!playback && !playback.isGap && offsetMs >= (playback.mediaStartOffsetMs ?? playback.startOffsetMs) - WINDOW_START_TOLERANCE_MS && offsetMs < playback.endOffsetMs
  }

  async function requestWindow(offsetMs: number, signal: AbortSignal, isPrefetch: boolean, qualityId?: string): Promise<PlaybackWindow | undefined>
  {
    for (let attempt = 0; ; attempt++)
    {
      signal.throwIfAborted()
      try
      {
        const playback = await options.loadWindow(offsetMs, { signal, prefetch: isPrefetch, qualityId })
        signal.throwIfAborted()
        return playback
      }
      catch (value)
      {
        signal.throwIfAborted()
        if (isPrefetch || !(value instanceof ApiError) || ![409, 425].includes(value.status) || attempt === 3)
        {
          throw value
        }
        await new Promise<void>((resolve, reject) =>
        {
          const abort = () =>
          {
            clearTimeout(timer)
            reject(signal.reason)
          }
          const timer = setTimeout(() => { signal.removeEventListener('abort', abort); resolve() }, RETRY_DELAY_MS)
          signal.addEventListener('abort', abort, { once: true })
        })
        signal.throwIfAborted()
      }
    }
  }

  function createRequest(offsetMs: number, isPrefetch: boolean, owner?: object, qualityId = requestedQualityId.value)
  {
    const controller = new AbortController()
    return { offsetMs, qualityId, controller, isPrefetch, owner, promise: requestWindow(offsetMs, controller.signal, isPrefetch, qualityId) }
  }

  function cancelPrefetch(allowRetry = false): void
  {
    if ((allowRetry && prefetchAttemptedFor === prefetch?.owner) || (warmedPrefetch && prefetchAttemptedFor === warmedPrefetch.owner)) { prefetchAttemptedFor = undefined }
    prefetch?.controller.abort()
    prefetch = undefined
    const prepared = warmedPrefetch
    warmedPrefetch = undefined
    prepared?.cleanup()
    prepared?.hls.destroy()
  }

  function warmPrefetch(playback: PlaybackWindow, owner: object, offsetMs: number): void
  {
    if (!Hls.isSupported() || !playback.url?.trim()) { return }
    const instance = new Hls({
      startPosition: windowPosition(offsetMs, playback.startOffsetMs),
      startFragPrefetch: true,
      maxBufferLength: PREVIEW_BUFFER_SECONDS,
      maxMaxBufferLength: PREVIEW_BUFFER_SECONDS,
      backBufferLength: BUFFER_LIMIT_SECONDS,
      frontBufferFlushThreshold: BUFFER_LIMIT_SECONDS,
      xhrSetup: xhr => { xhr.withCredentials = true },
    })
    const prepared = { playback, owner, offsetMs, hls: instance, initPts: undefined as InitPTSFoundData | undefined, cleanup: () => {} }
    const timestampFound = (_: unknown, data: InitPTSFoundData) =>
    {
      if (warmedPrefetch === prepared && data.id === 'main' && Number.isFinite(data.initPTS) && Number.isFinite(data.timescale) && data.timescale > 0)
      {
        prepared.initPts = data
      }
    }
    const failed = (_: unknown, data: { fatal: boolean }) =>
    {
      if (!data.fatal || warmedPrefetch !== prepared) { return }
      warmedPrefetch = undefined
      readyPrefetch = undefined
      readyPrefetchQualityId = undefined
      prepared.cleanup()
      instance.destroy()
    }
    prepared.cleanup = () =>
    {
      instance.off(Hls.Events.INIT_PTS_FOUND, timestampFound)
      instance.off(Hls.Events.ERROR, failed)
    }
    warmedPrefetch = prepared
    instance.on(Hls.Events.INIT_PTS_FOUND, timestampFound)
    instance.on(Hls.Events.ERROR, failed)
    instance.loadSource(getServerUrl(playback.url))
  }

  function applySettings(): void
  {
    if (!video.value) { return }
    video.value.volume = volume.value
    video.value.muted = muted.value
    video.value.playbackRate = rate.value
  }

  function syncBuffering(value: typeof source): void
  {
    if (!activeSource(value) || !value.hls || error.value) { return }
    const bufferTarget = playing.value ? BUFFER_TARGET_SECONDS : PREVIEW_BUFFER_SECONDS
    if (value.hls.config.maxBufferLength !== bufferTarget)
    {
      value.hls.config.maxBufferLength = bufferTarget
      value.hls.config.maxMaxBufferLength = playing.value ? BUFFER_LIMIT_SECONDS : PREVIEW_BUFFER_SECONDS
    }
    if (!playing.value && !value.previewPending)
    {
      if (value.hls.bufferingEnabled) { value.hls.pauseBuffering() }
      return
    }
    if (!value.hls.loadingEnabled)
    {
      value.hls.startLoad(mediaPosition(value, currentMs.value))
    }
    else if (!value.hls.bufferingEnabled)
    {
      value.hls.resumeBuffering()
    }
  }

  function playSource(value: typeof source): void
  {
    if (!activeSource(value) || !playing.value || loading.value || !video.value) { return }
    const operation = generation
    const attempt = ++playAttempt
    void video.value.play().catch(() =>
    {
      if (activeSource(value) && isCurrent(operation) && attempt === playAttempt)
      {
        playing.value = false
        cancelPrefetch(true)
        syncBuffering(value)
      }
    })
  }

  function positionSource(value: typeof source): void
  {
    const element = video.value
    if (!activeSource(value) || !element || !value.timestampReady || !value.needsPosition || element.readyState < 1) { return }
    const target = mediaPosition(value, pendingOffsetMs)
    applySettings()
    value.needsPosition = false
    value.awaitingSeek = Math.abs(element.currentTime - target) > 0.001
    if (value.awaitingSeek) { element.currentTime = target }
    loading.value = value.awaitingSeek || element.seeking || element.readyState < 2
    if (!loading.value) { value.previewPending = false }
    syncBuffering(value)
    playSource(value)
  }

  function sourceReady(value: typeof source): void
  {
    const element = video.value
    if (!activeSource(value) || !element || element.seeking || element.readyState < 2) { return }
    positionSource(value)
    if (value.needsPosition || element.seeking || element.readyState < 2 || (value.awaitingSeek && Math.abs(element.currentTime - mediaPosition(value, pendingOffsetMs)) > 0.25)) { return }
    value.awaitingSeek = false
    value.previewPending = false
    loading.value = false
    syncBuffering(value)
    playSource(value)
  }

  function mediaPosition(value: NonNullable<typeof source>, offsetMs: number): number
  {
    const target = windowPosition(offsetMs, value.startOffsetMs)
    const element = video.value
    let end = element && element.readyState >= 1 && Number.isFinite(element.duration) && element.duration >= 0
      ? element.duration : Number.POSITIVE_INFINITY
    const videoEndMs = value.playback.mediaEndTimestampMs
    if (value.playback.timestampMapping === 'HlsInitPts' && typeof videoEndMs === 'number' && Number.isFinite(videoEndMs))
    {
      const timestampOffsetMs = value.startOffsetMs - value.playback.startOffsetMs
      end = Math.min(end, (videoEndMs - timestampOffsetMs) / 1000)
    }
    return Math.min(target, Math.max(0, end - .001))
  }

  function durationChanged(value: typeof source): void
  {
    const element = video.value
    if (!activeSource(value) || !element || !value.timestampReady || element.readyState < 1 || error.value) { return }
    const positioning = value.needsPosition || value.awaitingSeek
    if (playing.value && !positioning) { return }
    if (!positioning) { pendingOffsetMs = currentMs.value }
    value.needsPosition = true
    value.previewPending = true
    loading.value = true
    positionSource(value)
    sourceReady(value)
  }

  function mapTimestamp(value: typeof source, data: InitPTSFoundData): void
  {
    if (!activeSource(value) || !video.value || value.playback.timestampMapping !== 'HlsInitPts'
      || data.id !== 'main' || !Number.isFinite(data.initPTS) || !Number.isFinite(data.timescale) || data.timescale <= 0) { return }
    const startOffsetMs = value.playback.startOffsetMs + data.initPTS / data.timescale * 1000
    if (!Number.isFinite(startOffsetMs) || (value.timestampReady && Math.abs(startOffsetMs - value.startOffsetMs) < .001)) { return }
    if (value.timestampReady && !value.needsPosition && !value.awaitingSeek && !loading.value)
    {
      pendingOffsetMs = playing.value
        ? Math.min(playbackPosition(video.value.currentTime, value.startOffsetMs, options.durationMs()), value.playback.endOffsetMs)
        : currentMs.value
      currentMs.value = pendingOffsetMs
    }
    value.startOffsetMs = startOffsetMs
    value.timestampReady = true
    value.needsPosition = true
    value.previewPending = true
    loading.value = true
    positionSource(value)
  }

  function sourceFailed(value: typeof source, status?: number): void
  {
    if (!activeSource(value)) { return }
    if (!value.originalFallback && status !== 401 && status !== 403 && (value.playback.effectiveQualityId ?? ORIGINAL_QUALITY_ID) !== ORIGINAL_QUALITY_ID)
    {
      void seek(currentMs.value, true, true)
      return
    }
    if (value.fromPrefetch && status !== 401 && status !== 403)
    {
      value.fromPrefetch = false
      readyPrefetch = undefined
      void seek(currentMs.value, true)
      return
    }
    loading.value = false
    playing.value = false
    value.previewPending = false
    value.hls?.stopLoad()
    error.value = t('components.player.loadFailed')
    cancelPrefetch()
  }

  function installSource(playback: PlaybackWindow, offsetMs: number, operation: number, fromPrefetch: boolean, originalFallback = false, prepared?: NonNullable<typeof warmedPrefetch>): void
  {
    const element = video.value
    if (!element || !isCurrent(operation)) { return }
    const sourceUrl = playback.url?.trim()
    if (!sourceUrl) { throw new Error(t('components.player.noVideo')) }
    const supportsHls = Hls.isSupported()
    if (!supportsHls && !element.canPlayType('application/vnd.apple.mpegurl')) { throw new Error(t('components.player.unsupported')) }
    const previous = source
    source = undefined
    previous?.cleanup()
    previous?.hls?.destroy()
    requestedQualityId.value ??= playback.requestedQualityId ?? ORIGINAL_QUALITY_ID
    effectiveQualityId.value = playback.effectiveQualityId ?? ORIGINAL_QUALITY_ID
    qualities.value = playback.qualities?.length ? playback.qualities : [{ id: ORIGINAL_QUALITY_ID, name: '', available: true, state: 'Ready' }]
    fallbackReason.value = playback.fallbackReason || (originalFallback ? 'Unavailable' : '')
    const installed = { playback, qualityId: requestedQualityId.value, operation, hls: undefined as Hls | undefined, startOffsetMs: playback.startOffsetMs, timestampReady: !supportsHls || playback.timestampMapping !== 'HlsInitPts', needsPosition: true, awaitingSeek: false, previewPending: true, fromPrefetch, originalFallback, cleanup: () => {} }
    if (prepared?.initPts && playback.timestampMapping === 'HlsInitPts')
    {
      installed.startOffsetMs += prepared.initPts.initPTS / prepared.initPts.timescale * 1000
      installed.timestampReady = true
    }
    source = installed
    serverGap.value = undefined
    const handlers = {
      loadedmetadata: () => positionSource(installed),
      durationchange: () => durationChanged(installed),
      loadeddata: () => sourceReady(installed),
      canplay: () => sourceReady(installed),
      seeked: () => sourceReady(installed),
      waiting: () => { if (activeSource(installed) && element.readyState < 3) { loading.value = true } },
      ended: () => { if (activeSource(installed) && playing.value && element.ended) { void seek(installed.playback.endOffsetMs) } },
      error: () => { if (!installed.hls && element.error) { sourceFailed(installed) } },
    }
    for (const [event, handler] of Object.entries(handlers)) { element.addEventListener(event, handler) }
    installed.cleanup = () => { for (const [event, handler] of Object.entries(handlers)) { element.removeEventListener(event, handler) } }
    applySettings()
    if (supportsHls)
    {
      const instance = prepared?.hls ?? new Hls({
        startPosition: windowPosition(offsetMs, playback.startOffsetMs),
        maxBufferLength: playing.value ? BUFFER_TARGET_SECONDS : PREVIEW_BUFFER_SECONDS,
        maxMaxBufferLength: playing.value ? BUFFER_LIMIT_SECONDS : PREVIEW_BUFFER_SECONDS,
        backBufferLength: BUFFER_LIMIT_SECONDS,
        frontBufferFlushThreshold: BUFFER_LIMIT_SECONDS,
        xhrSetup: xhr => { xhr.withCredentials = true },
      })
      prepared?.cleanup()
      instance.config.startFragPrefetch = false
      instance.config.maxBufferLength = playing.value ? BUFFER_TARGET_SECONDS : PREVIEW_BUFFER_SECONDS
      instance.config.maxMaxBufferLength = playing.value ? BUFFER_LIMIT_SECONDS : PREVIEW_BUFFER_SECONDS
      installed.hls = instance
      instance.on(Hls.Events.INIT_PTS_FOUND, (_, data) => mapTimestamp(installed, data))
      instance.on(Hls.Events.MANIFEST_PARSED, () => positionSource(installed))
      instance.on(Hls.Events.ERROR, (_, data) => { if (data.fatal) { sourceFailed(installed, data.response?.code ?? (data.networkDetails as { status?: number } | undefined)?.status) } })
      instance.attachMedia(element)
      if (!prepared) { instance.loadSource(getServerUrl(sourceUrl)) }
    }
    else
    {
      element.src = getServerUrl(sourceUrl)
      element.load()
    }
  }

  async function seek(value: number, force = false, originalFallback = false): Promise<void>
  {
    if (disposed) { return }
    const offset = clampOffset(value, options.durationMs())
    const operation = ++generation
    playAttempt++
    currentMs.value = offset
    pendingOffsetMs = offset
    error.value = ''
    const qualityId = originalFallback ? ORIGINAL_QUALITY_ID : requestedQualityId.value
    const reuseSource = !force && source?.qualityId === requestedQualityId.value && contains(source?.playback, offset)
    const prepared = !force && readyPrefetchQualityId === qualityId && contains(readyPrefetch, offset) ? readyPrefetch : undefined
    let preparedMedia = !reuseSource && prepared && warmedPrefetch?.playback === prepared && Math.abs(warmedPrefetch.offsetMs - offset) <= WINDOW_START_TOLERANCE_MS ? warmedPrefetch : undefined
    if (preparedMedia) { warmedPrefetch = undefined }
    let task = !force && !reuseSource && !prepared
      ? [foreground, prefetch].find(item => item?.offsetMs === offset && item.qualityId === qualityId && !item.controller.signal.aborted)
      : undefined
    if (foreground !== task) { foreground?.controller.abort() }
    foreground = undefined
    if (task && prefetch === task) { prefetch = undefined }
    else { cancelPrefetch() }
    if (force)
    {
      const previous = source
      source = undefined
      readyPrefetch = undefined
      prefetchAttemptedFor = undefined
      serverGap.value = undefined
      previous?.cleanup()
      previous?.hls?.destroy()
      if (video.value)
      {
        video.value.pause()
        video.value.removeAttribute('src')
        video.value.load()
      }
    }
    const knownGap = findGap(offset, normalizedGaps.value)
    if (options.durationMs() <= 0 || offset >= options.durationMs() || knownGap)
    {
      task?.controller.abort()
      preparedMedia?.cleanup()
      preparedMedia?.hls.destroy()
      video.value?.pause()
      source?.hls?.stopLoad()
      loading.value = false
      if (!knownGap) { playing.value = false }
      return
    }
    if (reuseSource && source)
    {
      const reused = source
      serverGap.value = undefined
      reused.operation = operation
      reused.needsPosition = true
      reused.previewPending = true
      loading.value = true
      await nextTick()
      if (!isCurrent(operation) || source !== reused) { return }
      positionSource(reused)
      syncBuffering(reused)
      return
    }
    loading.value = true
    video.value?.pause()
    source?.hls?.stopLoad()
    try
    {
      let playback = prepared
      let fromPrefetch = !!prepared
      if (!playback)
      {
        task ??= createRequest(offset, false, undefined, qualityId)
        for (;;)
        {
          foreground = task
          try
          {
            playback = await task.promise
          }
          catch (value)
          {
            if (!isCurrent(operation) || task.controller.signal.aborted) { return }
            if (!task.isPrefetch) { throw value }
          }
          if (!isCurrent(operation) || task.controller.signal.aborted) { return }
          if (task.isPrefetch && !playback)
          {
            task = createRequest(offset, false, undefined, qualityId)
            continue
          }
          fromPrefetch = task.isPrefetch
          break
        }
      }
      if (!isCurrent(operation)) { return }
      if (!playback) { throw new Error(t('components.player.noVideo')) }
      if (playback.isGap)
      {
        serverGap.value = { startMs: offset, endMs: playback.gapEndMs ?? playback.endOffsetMs }
        loading.value = false
        return
      }
      readyPrefetch = undefined
      await nextTick()
      if (!isCurrent(operation)) { return }
      installSource(playback, offset, operation, fromPrefetch, originalFallback, preparedMedia)
      preparedMedia = undefined
    }
    catch (value)
    {
      if (isCurrent(operation))
      {
        if (!originalFallback && qualityId && qualityId !== ORIGINAL_QUALITY_ID && !(value instanceof ApiError && [401, 403].includes(value.status)))
        {
          await seek(currentMs.value, true, true)
          return
        }
        error.value = value instanceof Error ? value.message : t('components.player.loadFailed')
        loading.value = false
        playing.value = false
      }
    }
    finally
    {
      preparedMedia?.cleanup()
      preparedMedia?.hls.destroy()
      if (isCurrent(operation) && foreground === task) { foreground = undefined }
    }
  }

  function maybePrefetch(): void
  {
    const current = source
    const element = video.value
    if (!activeSource(current) || !element || !playing.value || loading.value || error.value || scrubbing.value || element.seeking || element.readyState < 3 || document.visibilityState !== 'visible' || prefetch || warmedPrefetch || prefetchAttemptedFor === current) { return }
    const end = current.playback.endOffsetMs
    const remainingMs = (end - currentMs.value) / rate.value
    if (remainingMs <= 0 || remainingMs > PREFETCH_LEAD_MS || end >= options.durationMs() || findGap(end, normalizedGaps.value)) { return }
    prefetchAttemptedFor = current
    if (readyPrefetch)
    {
      if (contains(readyPrefetch, end)) { warmPrefetch(readyPrefetch, current, end) }
      return
    }
    const task = createRequest(end, true, current)
    prefetch = task
    void task.promise.then(playback =>
    {
      if (disposed || task.controller.signal.aborted || prefetch !== task) { return }
      if (contains(playback, end) && playback?.url?.trim())
      {
        readyPrefetch = playback
        readyPrefetchQualityId = task.qualityId
        warmPrefetch(playback, current, end)
      }
    }).catch(() => {}).finally(() => { if (prefetch === task) { prefetch = undefined } })
  }

  function tick(now: number): void
  {
    if (disposed) { return }
    const elapsed = lastFrameAt ? now - lastFrameAt : 0
    lastFrameAt = now
    if (playing.value && !scrubbing.value)
    {
      const activeGap = gap.value
      if (activeGap)
      {
        const target = clampOffset(currentMs.value + elapsed * rate.value, options.durationMs())
        currentMs.value = target
        if (target >= activeGap.endMs) { void seek(target) }
      }
      else if (activeSource(source) && video.value && !loading.value && !source.needsPosition && !source.awaitingSeek && !video.value.seeking)
      {
        const position = playbackPosition(video.value.currentTime, source.startOffsetMs, options.durationMs())
        currentMs.value = Math.min(position, source.playback.endOffsetMs)
        if (position >= source.playback.endOffsetMs - 30) { void seek(source.playback.endOffsetMs) }
      }
      maybePrefetch()
    }
    frame = requestAnimationFrame(tick)
  }

  async function togglePlay(): Promise<void>
  {
    if (disposed || error.value) { return }
    playing.value = !playing.value
    playAttempt++
    if (!playing.value)
    {
      video.value?.pause()
      cancelPrefetch(true)
      syncBuffering(source)
      return
    }
    if (currentMs.value >= options.durationMs())
    {
      await seek(options.isLive() ? Math.max(0, options.durationMs() - 2000) : 0)
      return
    }
    if (!gap.value && !contains(source?.playback, currentMs.value))
    {
      await seek(currentMs.value)
      return
    }
    syncBuffering(source)
    playSource(source)
  }

  async function selectQuality(id: string): Promise<void>
  {
    if (disposed || !qualities.value.some(quality => quality.id === id)) { return }
    if (requestedQualityId.value === id && effectiveQualityId.value === id && !error.value) { return }
    requestedQualityId.value = id
    fallbackReason.value = ''
    await seek(currentMs.value, true)
  }

  function visibilityChanged(): void
  {
    if (document.visibilityState !== 'visible') { cancelPrefetch(true) }
  }

  watch([volume, muted, rate], applySettings, { flush: 'sync' })
  watch(options.durationMs, (value, previous) =>
  {
    if (previous <= 0 && value > 0 && !source) { void seek(options.initialOffsetMs()) }
  })
  onMounted(() =>
  {
    document.addEventListener('visibilitychange', visibilityChanged)
    void seek(options.initialOffsetMs())
    frame = requestAnimationFrame(tick)
  })
  onBeforeUnmount(() =>
  {
    disposed = true
    generation++
    playAttempt++
    foreground?.controller.abort()
    cancelPrefetch()
    cancelAnimationFrame(frame)
    document.removeEventListener('visibilitychange', visibilityChanged)
    source?.cleanup()
    source?.hls?.destroy()
    video.value?.pause()
  })

  return { playing, loading, error, volume, muted, rate, gap, requestedQualityId, effectiveQualityId, qualities, fallbackReason, selectQuality, seek, togglePlay, retry: () => seek(currentMs.value, true) }
}
