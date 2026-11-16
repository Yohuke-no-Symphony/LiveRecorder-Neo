<template>
  <section class="translation-panel" :aria-label="t('translations.title')">
    <CardHeader :title="t('translations.title')">
      <template #suffix><span v-if="hasSnapshot" class="text-caption muted mono">{{ formatNumber(entries.length) }}</span></template>
      <template #actions>
        <v-btn icon="mdi-crosshairs-gps" variant="text" size="small" :disabled="!hasSeekableEntries" :aria-label="t('translations.locate')" :title="t('translations.locate')" @click="locate" />
        <v-btn icon="mdi-refresh" variant="text" size="small" :loading="loading" :disabled="!enabled" :aria-label="t('translations.refresh')" @click="refresh()" />
        <v-btn v-if="auth.canConfigureRecordings" icon="mdi-cog-outline" variant="text" size="small" :aria-label="t('shell.recordingConfiguration.title')" @click="emit('configure')" />
      </template>
    </CardHeader>
    <v-progress-linear v-if="loading" indeterminate color="primary" :aria-label="t('translations.loading')" />
    <v-alert v-if="error" type="error" density="compact" class="mx-4 my-3">{{ error }}</v-alert>
    <v-virtual-scroll v-if="entries.length" ref="entryScroll" class="translation-scroll" :items="entries" :item-height="88" item-key="sequence">
      <template #default="{ item }">
        <button type="button" class="translation-entry" :class="{ 'translation-entry-located': locatedSequence === item.sequence }" :aria-current="locatedSequence === item.sequence ? 'true' : undefined" :disabled="!canSeek(item.offsetMs)" :aria-label="t('translations.jump', { time: formatDuration(item.offsetMs, true), message: item.text })" @click="seek(item.offsetMs)">
          <span class="translation-meta"><span class="translation-author">{{ item.userName }}</span><span class="translation-time mono">{{ formatDuration(item.offsetMs, true) }}</span></span>
          <span class="translation-text">{{ item.text }}</span>
        </button>
      </template>
    </v-virtual-scroll>
    <div v-else-if="!loading && !error" class="translation-empty"><v-icon icon="mdi-translate" size="28" /><span>{{ enabled ? t('translations.empty') : t('components.events.notRecorded') }}</span></div>
  </section>
</template>

<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { computed, nextTick, ref, watch } from 'vue'
import { useAuthStore } from '@/stores/auth'
import CardHeader from '@/components/common/CardHeader.vue'
import { useSessionTranslations } from '@/composables/useSessionTranslations'
import { formatDuration, formatNumber } from '@/utilities/format'
import { findNearestPlaybackEntryIndex } from '@/utilities/nearestPlaybackEntry'

const props = withDefaults(defineProps<{ sessionId: string; durationMs: number; offsetMs?: number; enabled: boolean; isLive: boolean; active?: boolean; configurationRevision?: number }>(), { active: true, offsetMs: 0 })
const emit = defineEmits<{ seek: [offsetMs: number]; configure: [] }>()
const { t } = useI18n()
const auth = useAuthStore()
const { entries, loading, hasSnapshot, error, refresh } = useSessionTranslations(() => props.sessionId, () => props.active, () => props.enabled, () => props.isLive, () => props.configurationRevision)
const entryScroll = ref<{ scrollToIndex: (index: number, position: 'center') => void }>()
const locatedSequence = ref<number>()
const hasSeekableEntries = computed(() => entries.value.some(item => canSeek(item.offsetMs)))
watch(entries, () => { locatedSequence.value = undefined })

async function locate(): Promise<void>
{
  const index = findNearestPlaybackEntryIndex(entries.value, props.offsetMs, props.durationMs)
  if (index < 0)
  {
    return
  }
  const sequence = entries.value[index]!.sequence
  locatedSequence.value = sequence
  await nextTick()
  if (props.active && entries.value[index]?.sequence === sequence)
  {
    entryScroll.value?.scrollToIndex(index, 'center')
  }
}

function canSeek(offsetMs: number): boolean
{
  return Number.isFinite(offsetMs) && Number.isFinite(props.durationMs) && offsetMs >= 0 && offsetMs < props.durationMs
}

function seek(offsetMs: number): void
{
  if (canSeek(offsetMs))
  {
    emit('seek', offsetMs)
  }
}
</script>

<style scoped>
.translation-panel { display: flex; flex-direction: column; min-width: 0; min-height: 0; overflow: hidden; }
.translation-panel > .card-header { flex-shrink: 0; }
.translation-scroll { flex: 1; min-height: 0; overflow-y: auto; overscroll-behavior-y: contain; }
.translation-entry { display: flex; flex-direction: column; gap: 7px; width: 100%; padding: 14px var(--ui-card-padding); border: 0; border-top: 1px solid rgba(var(--v-theme-on-surface), .06); background: transparent; color: inherit; text-align: left; cursor: pointer; }
.translation-entry:hover:not(:disabled) { background: rgba(var(--v-theme-primary), .06); }
.translation-entry.translation-entry-located { background: rgba(var(--v-theme-primary), .12); box-shadow: inset 3px 0 rgb(var(--v-theme-primary)); }
.translation-entry:focus-visible { outline: 2px solid rgb(var(--v-theme-primary)); outline-offset: -2px; }
.translation-entry:disabled { cursor: default; opacity: .45; }
.translation-meta { display: flex; align-items: baseline; gap: 12px; width: 100%; min-width: 0; color: rgba(var(--v-theme-on-surface), .5); font-size: .7rem; }
.translation-author { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.translation-time { flex-shrink: 0; color: rgb(var(--v-theme-primary)); font-size: .65rem; }
.translation-text { width: 100%; white-space: pre-wrap; overflow-wrap: anywhere; font-size: .86rem; line-height: 1.65; }
.translation-empty { flex: 1; display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 14px; padding: 32px 16px; color: rgba(var(--v-theme-on-surface), .4); font-size: .8rem; }
</style>
