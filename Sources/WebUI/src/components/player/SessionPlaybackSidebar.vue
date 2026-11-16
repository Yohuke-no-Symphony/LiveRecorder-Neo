<template>
  <v-card class="session-playback-sidebar">
    <v-tabs v-model="tab" grow density="compact" color="primary">
      <v-tab value="events">{{ t('liveMarkers.eventsTab') }}</v-tab>
      <v-tab value="translations">{{ t('translations.tab') }}</v-tab>
      <v-tab value="markers">{{ t('liveMarkers.recordsTab') }}</v-tab>
    </v-tabs>
    <EventFeed v-show="tab === 'events'" class="sidebar-panel" :active="tab === 'events'" :session-id="sessionId" :offset-ms="offsetMs" :enabled="hasDanmu" :is-live="isLive" :navigation-revision="navigationRevision" :retention-count="auth.user?.playbackEventRetentionCount ?? DEFAULT_PLAYBACK_EVENT_RETENTION_COUNT" @seek="emit('seek', $event)">
      <template #settings><PlaybackEventSettings /></template>
    </EventFeed>
    <SessionTranslationPanel v-show="tab === 'translations'" class="sidebar-panel" :active="tab === 'translations'" :session-id="sessionId" :duration-ms="durationMs" :offset-ms="offsetMs" :enabled="hasDanmu" :is-live="isLive" :configuration-revision="configurationRevision" @seek="emit('seek', $event)" @configure="emit('configure')" />
    <SessionLiveMarkerPanel v-show="tab === 'markers'" class="sidebar-panel" :active="tab === 'markers'" :session-id="sessionId" :duration-ms="durationMs" :offset-ms="offsetMs" :editable="editable" :ended="ended" :file-id="liveMarkerFileId" @seek="emit('seek', $event)" />
  </v-card>
</template>

<script setup lang="ts">
import { ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useAuthStore } from '@/stores/auth'
import { DEFAULT_PLAYBACK_EVENT_RETENTION_COUNT } from '@/models/playbackPreferences'
import { provideSessionEventSnapshot } from '@/composables/useSessionEventSnapshot'
import EventFeed from './EventFeed.vue'
import PlaybackEventSettings from './PlaybackEventSettings.vue'
import SessionLiveMarkerPanel from './SessionLiveMarkerPanel.vue'
import SessionTranslationPanel from './SessionTranslationPanel.vue'

const props = withDefaults(defineProps<{ sessionId: string; offsetMs: number; durationMs: number; hasDanmu: boolean; isLive: boolean; navigationRevision: number; editable?: boolean; configurationRevision?: number; ended?: boolean; liveMarkerFileId?: string; showLiveMarkers?: boolean }>(), { editable: true })
const emit = defineEmits<{ seek: [offsetMs: number]; configure: [] }>()
const { t } = useI18n()
const auth = useAuthStore()
const tab = ref('events')
watch([() => props.sessionId, () => props.showLiveMarkers, () => props.liveMarkerFileId], () =>
{
  if (props.showLiveMarkers || props.liveMarkerFileId) { tab.value = 'markers' }
}, { immediate: true })
provideSessionEventSnapshot({ sessionId: () => props.sessionId, active: () => tab.value === 'events' || tab.value === 'translations', enabled: () => props.hasDanmu, isLive: () => props.isLive, configurationRevision: () => props.configurationRevision })
</script>

<style scoped>
.session-playback-sidebar { display: flex; flex-direction: column; min-width: 0; min-height: 0; overflow: hidden; }
.session-playback-sidebar > .v-tabs { flex: 0 0 auto; border-bottom: 1px solid rgba(var(--v-theme-on-surface), .07); }
.sidebar-panel { flex: 1; min-height: 0; border: 0; border-radius: 0; }
</style>
