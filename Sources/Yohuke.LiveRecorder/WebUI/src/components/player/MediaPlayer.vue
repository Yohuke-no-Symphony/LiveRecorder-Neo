<template>
  <section ref="player" class="media-player" tabindex="0" :aria-label="t('components.player.label')" @keydown="onKeydown">
    <div class="video-stage" @dblclick="fullscreen" @click="togglePlay">
      <video ref="video" crossorigin="use-credentials" playsinline preload="metadata" :muted="muted" @loadedmetadata="syncVideo" @canplay="onCanPlay" @waiting="onWaiting" @ended="onEnded" @error="onVideoError" />
      <div v-if="gap" class="video-overlay gap-overlay"><v-icon icon="mdi-video-off-outline" size="40" /><span>{{ t('components.events.gap') }}</span></div>
      <div v-else-if="error" class="video-overlay error-overlay" @click.stop><v-icon icon="mdi-alert-circle-outline" size="38" /><span>{{ error }}</span><v-btn variant="tonal" color="white" size="small" @click="retry">{{ t('components.common.retry') }}</v-btn></div>
      <div v-else-if="loading" class="video-overlay"><v-progress-circular indeterminate color="primary" size="38" /></div>
      <div v-else-if="!playing" class="video-overlay play-overlay"><v-icon icon="mdi-play" size="60" /></div>
    </div>
    <div class="player-controls">
      <div class="timeline-track">
        <div class="timeline-gaps" aria-hidden="true"><span v-for="item in normalizedGaps" :key="item.startMs" :style="{ left: `${item.startMs / durationMs * 100}%`, width: `${(item.endMs - item.startMs) / durationMs * 100}%` }" /></div>
        <v-slider :model-value="sliderOffset" :max="Math.max(durationMs, 1)" :step="1" color="primary" track-color="white" track-opacity="0.17" thumb-size="12" track-size="4" hide-details density="compact" :aria-label="t('components.player.position')" @update:model-value="scrub" @start="scrubbing = true" @end="finishScrub" />
      </div>
      <div class="d-flex align-center ga-1">
        <v-btn :icon="playing ? 'mdi-pause' : 'mdi-play'" :aria-label="playing ? t('components.player.pause') : t('components.player.play')" size="small" variant="text" color="white" @click="togglePlay" />
        <v-btn icon="mdi-rewind-10" size="small" variant="text" color="white" :aria-label="t('components.player.rewind')" @click="seek(currentMs - 10000)" />
        <v-btn icon="mdi-fast-forward-10" size="small" variant="text" color="white" :aria-label="t('components.player.forward')" @click="seek(currentMs + 10000)" />
        <span class="player-time mono">{{ formatDuration(currentMs, true) }}<span class="time-divider"> / </span>{{ formatDuration(durationMs, true) }}</span>
        <v-spacer />
        <v-btn v-if="isLive" variant="text" size="small" color="primary" @click="seek(Math.max(0, durationMs - 2000))"><span class="live-indicator" />{{ t('components.player.chaseLive') }}</v-btn>
        <v-menu><template #activator="{ props: menuProps }"><v-btn v-bind="menuProps" variant="text" size="small" color="white">{{ formatNumber(rate) }}×</v-btn></template><v-list density="compact"><v-list-item v-for="value in [0.5, 0.75, 1, 1.25, 1.5, 2]" :key="value" :title="`${formatNumber(value)}×`" @click="setRate(value)" /></v-list></v-menu>
        <v-btn :icon="muted || volume === 0 ? 'mdi-volume-off' : 'mdi-volume-high'" :aria-label="muted ? t('components.player.unmute') : t('components.player.mute')" variant="text" size="small" color="white" @click="muted = !muted" />
        <v-slider v-model="volume" :min="0" :max="1" :step="0.01" class="volume-slider" hide-details density="compact" color="white" thumb-size="8" track-size="3" :aria-label="t('components.player.volume')" @update:model-value="setVolume" />
        <v-btn icon="mdi-fullscreen" variant="text" size="small" color="white" :aria-label="t('components.player.fullscreen')" @click="fullscreen" />
      </div>
    </div>
  </section>
</template>

<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import Hls from 'hls.js'
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { ApiError } from '@/apis/APIBase'
import { getServerUrl } from '@/apis/ServerURL'
import type { PlaybackWindow, TimelineGap } from '@/models/media'
import { formatDuration, formatNumber } from '@/utilities/format'
import { clampOffset, findGap, normalizeTimelineGaps, playbackPosition, windowPosition } from '@/utilities/timeline'

const { t } = useI18n()

const props = defineProps<{ durationMs: number; gaps: TimelineGap[]; isLive: boolean; initialOffsetMs?: number; loadWindow: (offsetMs: number) => Promise<PlaybackWindow> }>()
const currentMs = defineModel<number>({ required: true })
const player = ref<HTMLElement>()
const video = ref<HTMLVideoElement>()
const playing = ref(false)
const loading = ref(false)
const error = ref('')
const volume = ref(0.8)
const muted = ref(false)
const rate = ref(1)
const sliderOffset = ref(0)
const scrubbing = ref(false)
const currentWindow = ref<PlaybackWindow>()
const serverGap = ref<TimelineGap>()
const normalizedGaps = computed(() => normalizeTimelineGaps(props.gaps, props.durationMs))
const gap = computed(() => findGap(currentMs.value, normalizedGaps.value) ?? (serverGap.value && currentMs.value >= serverGap.value.startMs && currentMs.value < serverGap.value.endMs ? serverGap.value : undefined))
let hls: Hls | undefined
let generation = 0
let sourceGeneration = -1
let frame = 0
let lastFrameAt = 0
let pendingOffsetMs = 0
let seekTimer: ReturnType<typeof setTimeout> | undefined
let disposed = false

watch(currentMs, value => { if (!scrubbing.value) { sliderOffset.value = value } })
watch(() => props.durationMs, (value, previous) => { if (previous <= 0 && value > 0 && !currentWindow.value) { void seek(props.initialOffsetMs ?? 0) } })
watch(muted, value => { if (video.value) { video.value.muted = value } })

function syncVideo(): void
{
  const element = video.value
  if (!element || !currentWindow.value || gap.value || sourceGeneration !== generation)
  {
    return
  }
  element.currentTime = windowPosition(pendingOffsetMs, currentWindow.value.startOffsetMs)
  element.volume = volume.value
  element.playbackRate = rate.value
  if (playing.value)
  {
    void element.play().catch(() => { playing.value = false })
  }
}

async function seek(value: number, force = false): Promise<void>
{
  const offset = clampOffset(value, props.durationMs)
  const operation = ++generation
  sourceGeneration = -1
  currentMs.value = offset
  pendingOffsetMs = offset
  error.value = ''
  if (offset >= props.durationMs && props.durationMs > 0)
  {
    playing.value = false
    video.value?.pause()
    return
  }
  const knownGap = findGap(offset, normalizedGaps.value)
  if (knownGap)
  {
    video.value?.pause()
    hls?.stopLoad()
    loading.value = false
    return
  }
  if (!force && currentWindow.value && offset >= currentWindow.value.startOffsetMs && offset < currentWindow.value.endOffsetMs && !currentWindow.value.isGap)
  {
    sourceGeneration = operation
    hls?.startLoad(windowPosition(offset, currentWindow.value.startOffsetMs))
    syncVideo()
    return
  }
  loading.value = true
  video.value?.pause()
  hls?.stopLoad()
  try
  {
    let playback: PlaybackWindow | undefined
    for (let attempt = 0; attempt < 4; attempt++)
    {
      try
      {
        playback = await props.loadWindow(offset)
        break
      }
      catch (value)
      {
        if (!(value instanceof ApiError) || ![409, 425].includes(value.status) || attempt === 3)
        {
          throw value
        }
        await new Promise(resolve => setTimeout(resolve, 1000))
        if (disposed || operation !== generation) { return }
      }
    }
    if (!playback || disposed || operation !== generation)
    {
      return
    }
    if (playback.isGap)
    {
      currentWindow.value = playback
      serverGap.value = { startMs: offset, endMs: playback.gapEndMs ?? playback.endOffsetMs }
      loading.value = false
      return
    }
    const sourceUrl = playback.url?.trim()
    if (!sourceUrl) { throw new Error(t('components.player.noVideo')) }
    currentWindow.value = playback
    serverGap.value = undefined
    hls?.destroy()
    hls = undefined
    await nextTick()
    const element = video.value
    if (!element) { return }
    sourceGeneration = operation
    if (Hls.isSupported())
    {
      const instance = new Hls({ startPosition: windowPosition(offset, playback.startOffsetMs), maxBufferLength: 30, xhrSetup: xhr => { xhr.withCredentials = true } })
      hls = instance
      instance.on(Hls.Events.MANIFEST_PARSED, () => { if (hls === instance) { syncVideo() } })
      instance.on(Hls.Events.ERROR, (_, data) =>
      {
        if (data.fatal && hls === instance && sourceGeneration === generation)
        {
          loading.value = false
          playing.value = false
          error.value = t('components.player.loadFailed')
        }
      })
      instance.attachMedia(element)
      instance.loadSource(getServerUrl(sourceUrl))
    }
    else if (element.canPlayType('application/vnd.apple.mpegurl'))
    {
      element.src = getServerUrl(sourceUrl)
      element.load()
    }
    else
    {
      throw new Error(t('components.player.unsupported'))
    }
  }
  catch (value)
  {
    if (operation === generation)
    {
      error.value = value instanceof Error ? value.message : t('components.player.loadFailed')
      loading.value = false
      playing.value = false
    }
  }
}

function tick(now: number): void
{
  const elapsed = lastFrameAt ? now - lastFrameAt : 0
  lastFrameAt = now
  if (playing.value && !scrubbing.value)
  {
    const activeGap = gap.value
    if (activeGap)
    {
      const target = clampOffset(currentMs.value + elapsed * rate.value, props.durationMs)
      currentMs.value = target
      if (target >= activeGap.endMs)
      {
        void seek(target)
      }
    }
    else if (video.value && currentWindow.value && !loading.value)
    {
      const position = playbackPosition(video.value.currentTime, currentWindow.value.startOffsetMs, props.durationMs)
      currentMs.value = Math.min(position, currentWindow.value.endOffsetMs)
      if (position >= currentWindow.value.endOffsetMs - 30)
      {
        void seek(currentWindow.value.endOffsetMs)
      }
    }
  }
  frame = requestAnimationFrame(tick)
}

async function togglePlay(): Promise<void>
{
  if (error.value) { return }
  playing.value = !playing.value
  if (!playing.value)
  {
    video.value?.pause()
    return
  }
  if (currentMs.value >= props.durationMs)
  {
    await seek(props.isLive ? Math.max(0, props.durationMs - 2000) : 0)
  }
  else if (!gap.value && (!currentWindow.value || currentWindow.value.isGap || currentMs.value >= currentWindow.value.endOffsetMs - 30 || currentMs.value < currentWindow.value.startOffsetMs))
  {
    await seek(currentMs.value)
  }
  if (!gap.value && !loading.value && video.value)
  {
    void video.value.play().catch(() => { playing.value = false })
  }
}

function scrub(value: number): void
{
  sliderOffset.value = value
  clearTimeout(seekTimer)
  if (!scrubbing.value)
  {
    seekTimer = setTimeout(() => { void seek(value) }, 180)
  }
}
function finishScrub(): void
{
  scrubbing.value = false
  void seek(sliderOffset.value)
}
function setRate(value: number): void
{
  rate.value = value
  if (video.value) { video.value.playbackRate = value }
}
function setVolume(value: number): void
{
  if (video.value) { video.value.volume = value }
  if (value > 0) { muted.value = false }
}
function onCanPlay(): void
{
  if (sourceGeneration !== generation || gap.value) { return }
  loading.value = false
  if (playing.value && !gap.value) { void video.value?.play().catch(() => { playing.value = false }) }
}
function onWaiting(): void
{
  if (!gap.value && sourceGeneration === generation) { loading.value = true }
}
function onEnded(): void
{
  if (currentWindow.value && sourceGeneration === generation) { void seek(currentWindow.value.endOffsetMs) }
}
function onVideoError(): void
{
  if (!loading.value && !hls)
  {
    error.value = t('components.player.loadFailed')
    playing.value = false
  }
}
function retry(): void
{
  void seek(currentMs.value, true)
}
function fullscreen(): void
{
  if (document.fullscreenElement) { void document.exitFullscreen() }
  else { void player.value?.requestFullscreen() }
}
function onKeydown(event: KeyboardEvent): void
{
  if ((event.target as HTMLElement).closest('input, button, [role="slider"]')) { return }
  if (event.code === 'Space') { event.preventDefault(); void togglePlay() }
  else if (event.key === 'ArrowRight') { event.preventDefault(); void seek(currentMs.value + 10000) }
  else if (event.key === 'ArrowLeft') { event.preventDefault(); void seek(currentMs.value - 10000) }
}
onMounted(() =>
{
  currentMs.value = clampOffset(props.initialOffsetMs ?? 0, props.durationMs)
  void seek(currentMs.value)
  frame = requestAnimationFrame(tick)
})
onBeforeUnmount(() =>
{
  disposed = true
  generation++
  cancelAnimationFrame(frame)
  clearTimeout(seekTimer)
  hls?.destroy()
  video.value?.pause()
})
defineExpose({ seek })
</script>

<style scoped>
.media-player { background: #08090c; border-radius: 16px; overflow: hidden; color: white; outline: none; }
.video-stage { position: relative; aspect-ratio: 16 / 9; cursor: pointer; background: #000; }
video { display: block; width: 100%; height: 100%; object-fit: contain; }
.video-overlay { position: absolute; inset: 0; display: flex; flex-direction: column; gap: 17px; align-items: center; justify-content: center; background: #0007; }
.gap-overlay { background: #0b0d11; color: #7d8594; font-size: .9rem; }
.error-overlay { font-size: .88rem; background: #101216; }
.play-overlay { background: #0002; }
.player-controls { padding: 5px 12px 9px; }
.timeline-track { position: relative; }
.timeline-gaps { position: absolute; left: 8px; right: 8px; top: 50%; height: 4px; transform: translateY(-50%); pointer-events: none; z-index: 1; }
.timeline-gaps span { position: absolute; height: 4px; background: #e2b76b; opacity: .55; }
.player-time { font-size: .72rem; padding: 0 5px; white-space: nowrap; color: #c8ccd5; }
.time-divider { color: #697181; padding: 0 3px; }
.volume-slider { max-width: 70px; min-width: 45px; margin: 0 7px; }
.live-indicator { display: inline-block; width: 5px; height: 5px; border-radius: 100%; background: currentColor; margin-right: 6px; }
.media-player:fullscreen { display: flex; flex-direction: column; border-radius: 0; }
.media-player:fullscreen .video-stage { flex: 1; aspect-ratio: auto; min-height: 0; }
@media(max-width: 600px) { .volume-slider { display: none; }.player-controls { padding-inline: 4px; }.player-time { font-size: .62rem; } }
</style>
