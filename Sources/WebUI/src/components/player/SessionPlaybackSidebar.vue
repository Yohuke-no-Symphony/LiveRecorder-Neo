<template>
  <v-card class="session-playback-sidebar">
    <v-tabs v-model="tab" grow density="compact" color="primary">
      <v-tab value="events">{{ t('liveMarkers.eventsTab') }}</v-tab>
      <v-tab value="markers">{{ t('liveMarkers.recordsTab') }}</v-tab>
    </v-tabs>
    <EventFeed v-show="tab === 'events'" class="sidebar-panel" :active="tab === 'events'" :session-id="sessionId" :offset-ms="offsetMs" :enabled="hasDanmu" :is-live="isLive" :navigation-revision="navigationRevision" @seek="emit('seek', $event)" />
    <SessionLiveMarkerPanel v-show="tab === 'markers'" class="sidebar-panel" :active="tab === 'markers'" :session-id="sessionId" :duration-ms="durationMs" :editable="editable" @seek="emit('seek', $event)" />
  </v-card>
</template>

<script setup lang="ts">
import { ref } from 'vue'
import { useI18n } from 'vue-i18n'
import EventFeed from './EventFeed.vue'
import SessionLiveMarkerPanel from './SessionLiveMarkerPanel.vue'

withDefaults(defineProps<{ sessionId: string; offsetMs: number; durationMs: number; hasDanmu: boolean; isLive: boolean; navigationRevision: number; editable?: boolean }>(), { editable: true })
const emit = defineEmits<{ seek: [offsetMs: number] }>()
const { t } = useI18n()
const tab = ref('events')
</script>

<style scoped>
.session-playback-sidebar { display: flex; flex-direction: column; min-width: 0; min-height: 0; overflow: hidden; }
.session-playback-sidebar > .v-tabs { flex: 0 0 auto; border-bottom: 1px solid rgba(var(--v-theme-on-surface), .07); }
.sidebar-panel { flex: 1; min-height: 0; border: 0; border-radius: 0; }
</style>
