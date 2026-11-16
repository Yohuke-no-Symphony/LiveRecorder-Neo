<template>
  <v-dialog :model-value="true" max-width="1000" scrollable :aria-label="title" @update:model-value="!$event && emit('close')">
    <ContentCard :title="title" class="transcoding-log-dialog">
      <div class="log-toolbar mb-3">
        <span v-if="!error || retrying" class="text-caption muted" role="status">{{ t(loading ? 'transcoding.logLoading' : retrying ? 'transcoding.logRetrying' : 'transcoding.logUpdating') }}</span>
        <v-spacer />
        <v-btn v-if="!following" variant="text" size="small" prepend-icon="mdi-arrow-down" @click="returnToLatest">{{ t('transcoding.logReturnToLatest') }}</v-btn>
      </div>
      <v-alert v-if="error" type="warning" density="compact" class="mb-3">{{ error }}</v-alert>
      <v-progress-linear v-if="loading" indeterminate color="primary" :aria-label="t('transcoding.logLoading')" class="mb-3" />
      <div ref="viewport" class="log-viewport" role="log" aria-live="off" :aria-label="title" tabindex="0" @scroll="onScroll">
        <p v-if="!entries.length && !loading && !error" class="text-caption muted pa-4">{{ t('transcoding.logEmpty') }}</p>
        <ol class="log-records">
          <li v-for="entry in entries" :key="entry.id" :data-log-id="entry.id" class="log-record" :class="`log-record-${severity(entry.level)}`">
            <p v-if="entry.droppedCount && entry.droppedCount > 0" class="log-dropped text-caption text-warning mb-2" role="status">{{ t('transcoding.logDropped', { count: formatNumber(entry.droppedCount) }) }}</p>
            <div class="log-metadata text-caption">
              <time :datetime="entry.timestampUtc" :title="entry.timestampUtc" class="mono muted">{{ formatTimestamp(entry.timestampUtc) }}</time>
              <span :class="`text-${severity(entry.level) === 'error' ? 'error' : severity(entry.level) === 'warning' ? 'warning' : 'medium-emphasis'}`">{{ levelLabel(entry.level) }}</span>
              <span v-if="entry.jobId" class="log-job muted">{{ t('transcoding.logJob') }} <code>{{ entry.jobId }}</code><template v-if="entry.attempt != null"> · {{ t('transcoding.logAttempt', { number: formatNumber(entry.attempt) }) }}</template></span>
            </div>
            <pre class="log-message mono">{{ entry.message }}</pre>
          </li>
        </ol>
      </div>
      <p v-if="entries.length === MAX_TRANSCODING_LOG_ENTRIES" class="text-caption muted mt-3">{{ t('transcoding.logLimit', { count: formatNumber(MAX_TRANSCODING_LOG_ENTRIES) }) }}</p>
      <template #actions><v-btn variant="text" @click="emit('close')">{{ t('transcoding.close') }}</v-btn></template>
    </ContentCard>
  </v-dialog>
</template>

<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { MAX_TRANSCODING_LOG_ENTRIES, useTranscodingLogs } from '@/composables/useTranscodingLogs'
import type { Worker } from '@/models/transcoding'
import { useAuthStore } from '@/stores/auth'
import { formatNumber } from '@/utilities/format'
import ContentCard from './ContentCard.vue'

const props = defineProps<{ worker: Pick<Worker, 'id' | 'name'> }>()
const emit = defineEmits<{ close: [] }>()
const auth = useAuthStore()
const { t, locale } = useI18n()
const { entries, loading, error, retrying } = useTranscodingLogs(() => props.worker.id)
const title = computed(() => t('transcoding.workerLogs', { name: props.worker.name }))
const viewport = ref<HTMLElement>()
const following = ref(true)
const timestampFormat = computed(() => new Intl.DateTimeFormat(locale.value, {
  year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false,
}))
let scrollRevision = 0

function severity(level: string): 'error' | 'warning' | 'information'
{
  if (level === 'Error' || level === 'Critical')
  {
    return 'error'
  }
  if (level === 'Warning')
  {
    return 'warning'
  }
  return 'information'
}

function levelLabel(level: string): string
{
  const keys = { error: 'logLevelError', warning: 'logLevelWarning', information: 'logLevelInformation' }
  return t(`transcoding.${keys[severity(level)]}`)
}

function formatTimestamp(value: string): string
{
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? '—' : timestampFormat.value.format(date)
}

function onScroll(): void
{
  const element = viewport.value
  if (element)
  {
    following.value = element.scrollHeight - element.clientHeight - element.scrollTop <= 8
  }
}

function returnToLatest(): void
{
  following.value = true
  if (viewport.value)
  {
    viewport.value.scrollTop = viewport.value.scrollHeight
  }
}

watch(entries, async () =>
{
  const revision = ++scrollRevision
  const element = viewport.value
  if (!element)
  {
    return
  }
  const shouldFollow = following.value
  const rows = Array.from(element.querySelectorAll<HTMLElement>('[data-log-id]'))
  const top = element.getBoundingClientRect().top
  const anchor = shouldFollow ? undefined : rows.find(row => row.getBoundingClientRect().bottom > top)
  const anchorId = anchor?.dataset.logId
  const anchorOffset = anchor ? anchor.getBoundingClientRect().top - top : 0
  await nextTick()
  if (revision !== scrollRevision || !viewport.value)
  {
    return
  }
  if (shouldFollow && following.value)
  {
    returnToLatest()
  }
  else if (anchorId)
  {
    const current = Array.from(viewport.value.querySelectorAll<HTMLElement>('[data-log-id]')).find(row => row.dataset.logId === anchorId)
    if (current)
    {
      viewport.value.scrollTop += current.getBoundingClientRect().top - viewport.value.getBoundingClientRect().top - anchorOffset
    }
    else
    {
      viewport.value.scrollTop = 0
    }
  }
})

watch(() => props.worker.id, () =>
{
  scrollRevision++
  following.value = true
}, { flush: 'sync' })
watch(() => auth.isAdmin, allowed =>
{
  if (!allowed)
  {
    emit('close')
  }
}, { immediate: true, flush: 'sync' })
onBeforeUnmount(() =>
{
  scrollRevision++
})
</script>

<style scoped>
.log-toolbar { display: flex; align-items: center; gap: 12px; min-height: 32px; }
.log-viewport { position: relative; height: min(56vh, 520px); min-height: 180px; overflow-y: auto; overflow-anchor: none; border: 1px solid rgba(var(--v-theme-on-surface), .12); border-radius: 8px; }
.log-records { margin: 0; padding: 0; list-style: none; }
.log-record { padding: 12px 14px; border-bottom: 1px solid rgba(var(--v-theme-on-surface), .08); }
.log-record:last-child { border-bottom: 0; }
.log-metadata { display: flex; align-items: baseline; flex-wrap: wrap; gap: 4px 12px; }
.log-job { overflow-wrap: anywhere; }
.log-message { margin: 5px 0 0; font-size: .8125rem; line-height: 1.6; white-space: pre-wrap; overflow-wrap: anywhere; }
.log-record-error { background: rgba(var(--v-theme-error), .05); }
.log-record-warning { background: rgba(var(--v-theme-warning), .05); }
</style>
