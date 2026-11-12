<template>
  <v-card :to="`/sessions/${session.id}`" class="session-card">
    <div class="session-cover">
      <v-img :src="MediaAPI.cover(session.id)" :aspect-ratio="16 / 9" cover>
        <template #error><div class="cover-fallback"><v-icon icon="mdi-play-box-outline" size="40" /></div></template>
      </v-img>
      <div class="cover-top"><v-chip v-if="session.state.toLowerCase() === 'recording'" color="error" variant="flat" size="x-small" prepend-icon="mdi-record">{{ t('components.status.recording') }}</v-chip></div>
      <span class="duration mono">{{ duration }}</span>
      <div class="cover-play"><v-icon icon="mdi-play" size="34" /></div>
    </div>
    <div class="session-info">
      <h3 class="session-title">{{ session.title || t('components.library.unnamedSession') }}</h3>
      <div class="d-flex align-center justify-space-between ga-2 mt-3">
        <span class="text-caption muted text-truncate">{{ streamerName || formatDate(session.startedAtUtc) }}</span>
        <v-icon v-if="session.hasDanmu" icon="mdi-comment-text-outline" size="15" color="secondary" :aria-label="t('components.library.hasDanmu')" />
      </div>
      <div v-if="streamerName" class="text-caption muted mt-1">{{ formatDate(session.startedAtUtc) }}</div>
    </div>
  </v-card>
</template>

<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { computed } from 'vue'
import { MediaAPI } from '@/apis/MediaAPI'
import type { BroadcastSession } from '@/models/media'
import { formatDate, formatDuration } from '@/utilities/format'

const { t } = useI18n()

const props = defineProps<{ session: BroadcastSession; streamerName?: string }>()
const duration = computed(() => formatDuration(Math.max(0, new Date(props.session.endedAtUtc ?? props.session.lastMediaAtUtc).getTime() - new Date(props.session.startedAtUtc).getTime())))
</script>

<style scoped>
.session-card { overflow: hidden; transition: transform .18s ease, border-color .18s ease; }
.session-card:hover { transform: translateY(-3px); border-color: rgba(var(--v-theme-primary), .4); }
.session-cover { position: relative; background: rgba(var(--v-theme-on-surface), .035); }
.cover-fallback { display: grid; place-items: center; height: 100%; color: rgba(var(--v-theme-on-surface), .15); }
.cover-top { position: absolute; left: 12px; top: 12px; }
.duration { position: absolute; bottom: 10px; right: 10px; font-size: .7rem; color: white; border-radius: 5px; padding: 3px 6px; background: #000a; }
.cover-play { display: grid; place-items: center; position: absolute; inset: 0; opacity: 0; color: white; background: #0003; transition: opacity .18s; }
.session-card:hover .cover-play { opacity: 1; }
.session-info { padding: 17px 18px 18px; }
.session-title { overflow: hidden; display: -webkit-box; -webkit-line-clamp: 2; -webkit-box-orient: vertical; font-size: .94rem; font-weight: 550; line-height: 1.55; min-height: 2.9em; }
</style>
