<template>
  <v-card class="clip-card">
    <router-link :to="{ path: `/sessions/${encodeURIComponent(clip.sessionId)}`, query: { at: clip.startMs } }"
                 class="clip-cover" :aria-label="clip.title">
      <v-img :src="imageSource" :aspect-ratio="4 / 3" cover @error="retry" @load="loaded">
        <template #error>
          <div class="cover-fallback">
            <v-icon icon="mdi-play-box-outline" size="40"/>
          </div>
        </template>
      </v-img>
      <span class="clip-length mono">{{ duration }}</span>
      <span class="cover-play"><v-icon icon="mdi-play" size="34"/></span>
    </router-link>
    <div class="clip-info">
      <h3 class="clip-title" :title="clip.title">{{ clip.title }}</h3>
      <div class="clip-range mono muted">{{ formatDuration(clip.startMs, true) }} — {{ formatDuration(clip.endMs, true)
        }}
      </div>
      <div class="clip-details muted">
        <span class="clip-size">{{ formatBytes(clip.bytes) }}</span>
        <time class="clip-expiry" :datetime="clip.expiresAtUtc">{{ t('views.clips.expires', { date:
          formatDate(clip.expiresAtUtc) }) }}
        </time>
      </div>
      <div class="clip-actions">
        <v-tooltip :text="t('views.common.play')" location="top">
          <template #activator="{ props: tooltipProps }">
            <v-btn v-bind="tooltipProps" icon="mdi-play" color="primary" size="small"
                   :aria-label="t('views.common.play')" :disabled="unavailable || !auth.canViewRecordings"
                   @click="emit('preview')"/>
          </template>
        </v-tooltip>
        <v-tooltip :text="t('views.common.download')" location="top">
          <template #activator="{ props: tooltipProps }">
            <v-btn v-bind="tooltipProps" icon="mdi-download" size="small" variant="tonal"
                   :aria-label="t('views.common.download')"
                   :href="canDownload ? MediaAPI.downloadClip(clip.id) : undefined" :disabled="!canDownload"/>
          </template>
        </v-tooltip>
        <v-tooltip :text="t('sharing.clipTitle')" location="top">
          <template #activator="{ props: tooltipProps }">
            <v-btn v-bind="tooltipProps" class="clip-share-action" icon="mdi-share-variant-outline" size="small"
                   variant="text" :aria-label="t('sharing.clipTitle')" :disabled="unavailable" @click="emit('share')"/>
          </template>
        </v-tooltip>
        <v-tooltip :text="t('views.common.moveToTrash')" location="top">
          <template #activator="{ props: tooltipProps }">
            <v-btn v-bind="tooltipProps" icon="mdi-delete-outline" size="small" variant="text"
                   :aria-label="t('views.common.moveToTrash')" @click="emit('trash')"/>
          </template>
        </v-tooltip>
      </div>
    </div>
  </v-card>
</template>

<script setup lang="ts">
import {computed} from 'vue'
import {useI18n} from 'vue-i18n'
import {MediaAPI} from '@/apis/MediaAPI'
import {useRetryableImage} from '@/composables/useRetryableImage'
import {useAuthStore} from '@/stores/auth'
import type {Clip} from '@/models/media'
import {formatBytes, formatDate, formatDuration} from '@/utilities/format'

const props = defineProps<{ clip: Clip }>()
const emit = defineEmits<{ preview: []; share: []; trash: [] }>()
const {t} = useI18n()
const auth = useAuthStore()
const {
  imageSource,
  retry,
  loaded
} = useRetryableImage(() => auth.canViewRecordings ? MediaAPI.cover(props.clip.sessionId) : undefined)
const duration = computed(() => formatDuration(props.clip.endMs - props.clip.startMs))
const unavailable = computed(() => props.clip.isTrashed || new Date(props.clip.expiresAtUtc).getTime() <= Date.now())
const canDownload = computed(() => !unavailable.value && auth.canDownloadRecordings)
</script>

<style scoped>
.clip-card {
  display: flex;
  flex-direction: column;
  overflow: hidden;
  transition: transform .18s ease, border-color .18s ease;
}

.clip-card:hover {
  transform: translateY(-3px);
  border-color: rgba(var(--v-theme-primary), .4);
}

.clip-cover {
  display: block;
  position: relative;
  color: inherit;
  background: rgba(var(--v-theme-on-surface), .035);
}

.clip-cover:focus-visible {
  outline: 2px solid rgb(var(--v-theme-primary));
  outline-offset: -3px;
}

.cover-fallback {
  display: grid;
  place-items: center;
  height: 100%;
  color: rgba(var(--v-theme-on-surface), .15);
}

.clip-length {
  position: absolute;
  right: 10px;
  bottom: 10px;
  background: #000a;
  -webkit-backdrop-filter: var(--ui-backdrop-blur);
  backdrop-filter: var(--ui-backdrop-blur);
  color: white;
  font-size: .7rem;
  padding: 3px 6px;
  border-radius: 5px;
}

.cover-play {
  display: grid;
  place-items: center;
  position: absolute;
  inset: 0;
  opacity: 0;
  color: white;
  background: #0003;
  transition: opacity .18s;
}

.clip-cover:hover .cover-play, .clip-cover:focus-visible .cover-play {
  opacity: 1;
}

.clip-info {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 4px;
  min-width: 0;
  padding: 12px 16px 14px;
}

.clip-title {
  margin: 0;
  overflow: hidden;
  display: -webkit-box;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
  font-size: .94rem;
  font-weight: 550;
  line-height: 1.4;
}

.clip-range, .clip-details {
  font-size: .75rem;
  line-height: 1.5;
}

.clip-range {
  white-space: nowrap;
}

.clip-details {
  display: flex;
  flex-wrap: wrap;
  justify-content: space-between;
  gap: 4px 12px;
}

.clip-size, .clip-expiry {
  white-space: nowrap;
}

.clip-actions {
  display: flex;
  align-items: center;
  gap: 4px;
  margin-top: auto;
  padding-top: 8px;
}

.clip-share-action {
  margin-inline-start: auto;
}
</style>
