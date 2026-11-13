<template>
  <v-card class="session-card" :class="{ 'is-selected': selected }" @contextmenu="openContextMenu" @keydown="onMenuKeydown">
    <router-link v-slot="{ href, navigate }" :to="`/sessions/${session.id}`" custom>
      <a :href="href" class="session-link" :role="selectionMode ? 'button' : undefined" :aria-pressed="selectionMode ? selected : undefined" :aria-disabled="selectionMode && !canSelect ? true : undefined" @click="onClick($event, navigate)" @keydown.space="onSelectionKeydown">
        <div class="session-cover">
          <v-img :src="MediaAPI.cover(session.id)" :aspect-ratio="4 / 3" cover>
            <template #error><div class="cover-fallback"><v-icon icon="mdi-play-box-outline" size="40" /></div></template>
          </v-img>
          <div class="cover-top"><v-chip v-if="session.state.toLowerCase() === 'recording'" color="error" variant="flat" size="x-small" prepend-icon="mdi-record">{{ t('components.status.recording') }}</v-chip></div>
          <span class="duration mono">{{ duration }}</span>
          <div class="cover-play"><v-icon icon="mdi-play" size="34" /></div>
        </div>
        <div class="session-info">
          <h3 class="session-title">{{ session.title || t('components.library.unnamedSession') }}</h3>
          <div class="d-flex align-center justify-space-between ga-2">
            <span class="text-caption muted text-truncate">{{ streamerName || formatDate(session.startedAtUtc) }}</span>
            <v-icon v-if="session.hasDanmu" icon="mdi-comment-text-outline" size="15" color="secondary" :aria-label="t('components.library.hasDanmu')" />
          </div>
          <div v-if="streamerName" class="text-caption muted">{{ formatDate(session.startedAtUtc) }}</div>
        </div>
      </a>
    </router-link>
    <v-checkbox-btn v-if="selectionMode" class="session-select" :model-value="selected" :disabled="disabled || !canSelect" :aria-label="t('components.library.selectSession', { title: session.title || t('components.library.unnamedSession') })" @update:model-value="emit('toggle')" />
    <v-btn v-if="allowActions" class="session-actions" icon="mdi-dots-horizontal" size="small" variant="flat" :disabled="disabled" aria-haspopup="menu" :aria-label="t('components.library.actionsForSession', { title: session.title || t('components.library.unnamedSession') })" @click.stop="emit('actions', $event)" />
  </v-card>
</template>

<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { computed } from 'vue'
import { MediaAPI } from '@/apis/MediaAPI'
import type { BroadcastSession } from '@/models/media'
import { formatDate, formatDuration } from '@/utilities/format'

const { t } = useI18n()

const props = defineProps<{ session: BroadcastSession; streamerName?: string; allowActions?: boolean; selectionMode?: boolean; selected?: boolean; canSelect?: boolean; disabled?: boolean }>()
const emit = defineEmits<{ actions: [event: MouseEvent | KeyboardEvent]; toggle: [] }>()
const duration = computed(() => formatDuration(Math.max(0, new Date(props.session.endedAtUtc ?? props.session.lastMediaAtUtc).getTime() - new Date(props.session.startedAtUtc).getTime())))

function onClick(event: MouseEvent, navigate: (event?: MouseEvent) => unknown): void
{
  if (props.disabled) { event.preventDefault(); return }
  if (!props.selectionMode) { navigate(event); return }
  event.preventDefault()
  if (props.canSelect && !props.disabled) { emit('toggle') }
}

function onSelectionKeydown(event: KeyboardEvent): void
{
  if (!props.selectionMode) { return }
  event.preventDefault()
  if (props.canSelect && !props.disabled) { emit('toggle') }
}

function openContextMenu(event: MouseEvent): void
{
  if (!props.allowActions) { return }
  event.preventDefault()
  event.stopPropagation()
  if (!props.disabled) { emit('actions', event) }
}

function onMenuKeydown(event: KeyboardEvent): void
{
  if (event.key !== 'ContextMenu' && !(event.shiftKey && event.key === 'F10')) { return }
  if (!props.allowActions) { return }
  event.preventDefault()
  event.stopPropagation()
  if (!props.disabled) { emit('actions', event) }
}
</script>

<style scoped>
.session-card { position: relative; overflow: hidden; transition: transform .18s ease, border-color .18s ease; }
.session-card:hover { transform: translateY(-3px); border-color: rgba(var(--v-theme-primary), .4); }
.session-card.is-selected { border-color: rgb(var(--v-theme-primary)); box-shadow: 0 0 0 1px rgb(var(--v-theme-primary)); }
.session-link { display: block; color: inherit; text-decoration: none; }
.session-link:focus-visible { outline: 2px solid rgb(var(--v-theme-primary)); outline-offset: -3px; }
.session-actions { position: absolute; top: 8px; right: 8px; color: white; background: #0008; }
.session-select { position: absolute; top: 8px; left: 8px; border-radius: 6px; background: rgb(var(--v-theme-surface)); }
.session-card:has(.session-select) .cover-top { top: 56px; }
.session-cover { position: relative; background: rgba(var(--v-theme-on-surface), .035); }
.cover-fallback { display: grid; place-items: center; height: 100%; color: rgba(var(--v-theme-on-surface), .15); }
.cover-top { position: absolute; left: 12px; top: 12px; }
.duration { position: absolute; bottom: 10px; right: 10px; font-size: .7rem; color: white; border-radius: 5px; padding: 3px 6px; background: #000a; }
.cover-play { display: grid; place-items: center; position: absolute; inset: 0; opacity: 0; color: white; background: #0003; transition: opacity .18s; }
.session-card:hover .cover-play { opacity: 1; }
.session-info { display: flex; flex-direction: column; gap: 4px; padding: 12px 16px 14px; }
.session-title { margin: 0; overflow: hidden; display: -webkit-box; -webkit-line-clamp: 2; -webkit-box-orient: vertical; font-size: .94rem; font-weight: 550; line-height: 1.4; }
</style>
