<template>
  <VideoCard :title="clip.title" :duration-ms="clip.endMs - clip.startMs"
             :cover-url="auth.canViewRecordings ? MediaAPI.clipCover(clip.id) : undefined"
             :to="{ path: `/sessions/${encodeURIComponent(clip.sessionId)}`, query: { at: clip.startMs } }">
    <template #metadata>
      <span class="clip-range mono">{{ formatDuration(clip.startMs, true) }} — {{ formatDuration(clip.endMs, true) }}</span>
      <span>{{ formatBytes(clip.bytes) }}</span>
      <time :datetime="clip.expiresAtUtc">{{ t('views.clips.expires', { date: formatDate(clip.expiresAtUtc) }) }}</time>
    </template>
    <template #actions>
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
          <v-btn v-bind="tooltipProps" class="ms-auto" icon="mdi-share-variant-outline" size="small"
                 variant="text" :aria-label="t('sharing.clipTitle')" :disabled="unavailable" @click="emit('share')"/>
        </template>
      </v-tooltip>
      <v-tooltip :text="t('views.common.moveToTrash')" location="top">
        <template #activator="{ props: tooltipProps }">
          <v-btn v-bind="tooltipProps" icon="mdi-delete-outline" size="small" variant="text"
                 :aria-label="t('views.common.moveToTrash')" @click="emit('trash')"/>
        </template>
      </v-tooltip>
    </template>
  </VideoCard>
</template>

<script setup lang="ts">
import {computed} from 'vue'
import {useI18n} from 'vue-i18n'
import {MediaAPI} from '@/apis/MediaAPI'
import VideoCard from '@/components/common/VideoCard.vue'
import {useAuthStore} from '@/stores/auth'
import type {Clip} from '@/models/media'
import {formatBytes, formatDate, formatDuration} from '@/utilities/format'

const props = defineProps<{ clip: Clip }>()
const emit = defineEmits<{ preview: []; share: []; trash: [] }>()
const {t} = useI18n()
const auth = useAuthStore()
const unavailable = computed(() => props.clip.isTrashed || new Date(props.clip.expiresAtUtc).getTime() <= Date.now())
const canDownload = computed(() => !unavailable.value && auth.canDownloadRecordings)
</script>

<style scoped>
.clip-range {
  flex-basis: 100%;
}
</style>
