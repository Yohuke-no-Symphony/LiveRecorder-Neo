<template>
  <section ref="player" class="media-player" tabindex="0" :aria-label="t('components.player.label')" @keydown="onKeydown">
    <div class="video-stage" @dblclick="fullscreen" @click="togglePlay">
      <video ref="video" crossorigin="use-credentials" playsinline preload="metadata" :muted="muted" />
      <div v-if="gap" class="video-overlay gap-overlay"><v-icon icon="mdi-video-off-outline" size="40" /><span>{{ t('components.events.gap') }}</span></div>
      <div v-else-if="error" class="video-overlay error-overlay" @click.stop><v-icon icon="mdi-alert-circle-outline" size="38" /><span>{{ error }}</span><v-btn variant="tonal" color="white" size="small" @click="retry">{{ t('components.common.retry') }}</v-btn></div>
      <div v-else-if="loading" class="video-overlay"><v-progress-circular indeterminate color="primary" size="38" /></div>
      <div v-else-if="!playing" class="video-overlay play-overlay"><v-icon icon="mdi-play" size="60" /></div>
    </div>
    <div class="player-controls">
      <PlaybackTimeline :model-value="sliderOffset" :duration-ms="durationMs" :gaps="gaps" :timeline="timeline" :bookmarks="bookmarks" :selection="selection" :loading="timelineLoading" :error="timelineError" @update:model-value="scrub" @start="startScrub" @end="finishScrub" @seek="navigate" @retry="emit('refreshTimeline')" />
      <div class="player-toolbar d-flex align-center ga-1">
        <v-btn :icon="playing ? 'mdi-pause' : 'mdi-play'" :aria-label="playing ? t('components.player.pause') : t('components.player.play')" size="small" variant="text" color="white" @click="togglePlay" />
        <v-btn icon="mdi-rewind-10" size="small" variant="text" color="white" :aria-label="t('components.player.rewind')" @click="navigate(currentMs - 10000)" />
        <v-btn icon="mdi-fast-forward-10" size="small" variant="text" color="white" :aria-label="t('components.player.forward')" @click="navigate(currentMs + 10000)" />
        <slot name="actions" :current-ms="currentMs" />
        <span class="player-time mono">{{ formatDuration(currentMs, true) }}<span class="time-divider"> / </span>{{ formatDuration(durationMs, true) }}</span>
        <v-spacer />
        <v-btn v-if="isLive" variant="text" size="small" color="primary" @click="navigate(Math.max(0, durationMs - 2000))"><span class="live-indicator" />{{ t('components.player.chaseLive') }}</v-btn>
        <v-menu v-if="qualities.length > 1"><template #activator="{ props: menuProps }"><v-btn v-bind="menuProps" variant="text" size="small" color="white" :aria-label="`${t('transcoding.quality')}: ${qualityName(effectiveQualityId)}`" :title="fallbackReason ? t('transcoding.fallback', { quality: qualityName(requestedQualityId || 'original') }) : undefined">{{ qualityName(effectiveQualityId) }}<v-icon v-if="fallbackReason" icon="mdi-information-outline" size="14" class="ml-1" /></v-btn></template><v-list density="compact"><v-list-item v-for="quality in qualities" :key="quality.id" :title="qualityName(quality.id)" :subtitle="quality.available ? undefined : t('transcoding.unavailable')" :active="quality.id === requestedQualityId" @click="changeQuality(quality.id)" /></v-list></v-menu>
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
import { nextTick, onBeforeUnmount, ref, watch } from 'vue'
import PlaybackTimeline from '@/components/player/PlaybackTimeline.vue'
import { usePlaybackController } from '@/composables/usePlaybackController'
import type { SessionTimeline } from '@/models/sessionTimeline'
import type { SessionBookmark } from '@/models/sessionBookmark'
import type { ClipSelection } from '@/models/clipSelection'
import type { PlaybackWindow, TimelineGap } from '@/models/media'
import type { PlaybackLoadOptions } from '@/models/playbackLoadOptions'
import { formatDuration, formatNumber } from '@/utilities/format'

const { t } = useI18n()
const props = defineProps<{ durationMs: number; gaps: TimelineGap[]; isLive: boolean; initialOffsetMs?: number; loadWindow: (offsetMs: number, options?: PlaybackLoadOptions) => Promise<PlaybackWindow | undefined>; timeline?: SessionTimeline; bookmarks?: SessionBookmark[]; selection?: ClipSelection; timelineLoading?: boolean; timelineError?: string; fullscreenTarget?: HTMLElement }>()
const emit = defineEmits<{ navigate: []; refreshTimeline: [] }>()
const currentMs = defineModel<number>({ required: true })
const player = ref<HTMLElement>()
const video = ref<HTMLVideoElement>()
const sliderOffset = ref(0)
const scrubbing = ref(false)
const { playing, loading, error, volume, muted, rate, gap, requestedQualityId, effectiveQualityId, qualities, fallbackReason, selectQuality, seek, togglePlay, retry } = usePlaybackController({
  video, currentMs, scrubbing,
  durationMs: () => props.durationMs,
  gaps: () => props.gaps,
  isLive: () => props.isLive,
  initialOffsetMs: () => props.initialOffsetMs ?? 0,
  loadWindow: (offsetMs, options) => props.loadWindow(offsetMs, options),
})
let scrubRevision = 0

watch(currentMs, value => { if (!scrubbing.value) { sliderOffset.value = value } })

async function navigate(value: number, force = false): Promise<void>
{
  scrubRevision++
  if (force) { scrubbing.value = false }
  emit('navigate')
  await seek(value >= props.durationMs ? Math.max(0, props.durationMs - 1) : value, force)
}

async function scrub(value: number): Promise<void>
{
  sliderOffset.value = value
  const revision = ++scrubRevision
  await nextTick()
  if (!scrubbing.value && revision === scrubRevision)
  {
    await navigate(value)
  }
}
function startScrub(): void
{
  scrubRevision++
  scrubbing.value = true
}
function finishScrub(): void
{
  scrubbing.value = false
  void navigate(sliderOffset.value)
}
function qualityName(id: string): string
{
  return id === 'original' ? t('transcoding.original') : qualities.value.find(quality => quality.id === id)?.name || id
}
async function changeQuality(id: string): Promise<void>
{
  scrubRevision++
  scrubbing.value = false
  emit('navigate')
  await selectQuality(id)
}
function setRate(value: number): void
{
  rate.value = value
}
function setVolume(value: number): void
{
  if (value > 0) { muted.value = false }
}
function fullscreen(): void
{
  if (document.fullscreenElement) { void document.exitFullscreen() }
  else { void (props.fullscreenTarget ?? player.value)?.requestFullscreen() }
}
function onKeydown(event: KeyboardEvent): void
{
  if ((event.target as HTMLElement).closest('input, button, [role="slider"]')) { return }
  if (event.code === 'Space') { event.preventDefault(); void togglePlay() }
  else if (event.key === 'ArrowRight') { event.preventDefault(); void navigate(currentMs.value + 10000) }
  else if (event.key === 'ArrowLeft') { event.preventDefault(); void navigate(currentMs.value - 10000) }
}
onBeforeUnmount(() => { scrubRevision++ })
defineExpose({ seek: navigate, reload: (value: number) => navigate(value, true) })
</script>

<style scoped>
.media-player { background: #08090c; border-radius: 16px; overflow: hidden; color: white; outline: none; }
.video-stage { position: relative; aspect-ratio: 16 / 9; cursor: pointer; background: #000; }
video { display: block; width: 100%; height: 100%; object-fit: contain; }
.video-overlay { position: absolute; inset: 0; display: flex; flex-direction: column; gap: 17px; align-items: center; justify-content: center; background: #0007; }
.gap-overlay { background: #0b0d11; color: #7d8594; font-size: .9rem; }
.error-overlay { font-size: .88rem; background: #101216; }
.play-overlay { background: #0002; }
.player-controls { flex: 0 0 auto; padding: 5px 12px 9px; }
.player-toolbar { flex-wrap: wrap; }
.player-time { font-size: .72rem; padding: 0 5px; white-space: nowrap; color: #c8ccd5; }
.time-divider { color: #697181; padding: 0 3px; }
.volume-slider { max-width: 70px; min-width: 45px; margin: 0 7px; }
.live-indicator { display: inline-block; width: 5px; height: 5px; border-radius: 100%; background: currentColor; margin-right: 6px; }
.media-player:fullscreen, :fullscreen .media-player { display: flex; flex-direction: column; height: 100%; min-height: 0; border-radius: 0; }
.media-player:fullscreen .video-stage, :fullscreen .media-player .video-stage { flex: 1; aspect-ratio: auto; min-height: 0; }
@media(max-width: 600px) { .volume-slider { display: none; }.player-controls { padding-inline: 4px; }.player-time { font-size: .62rem; } }
</style>
