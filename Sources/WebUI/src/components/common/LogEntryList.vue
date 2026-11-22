<template>
  <div class="log-entry-list" :class="{ 'log-entry-list-compact': compact, 'log-entry-list-fill': fillHeight }">
    <div class="log-toolbar mb-3">
      <slot name="search"/>
      <span v-if="!error || retrying" class="text-caption muted" role="status">{{ t(loading ? 'transcoding.logLoading' : retrying ? 'transcoding.logRetrying' : 'transcoding.logUpdating') }}</span>
      <v-spacer/>
      <v-btn v-if="!following" variant="text" size="small" prepend-icon="mdi-arrow-down" @click="returnToLatest">{{
        t('transcoding.logReturnToLatest') }}
      </v-btn>
    </div>
    <v-alert v-if="error" type="warning" density="compact" class="mb-3">{{ error }}</v-alert>
    <v-progress-linear v-if="loading" indeterminate color="primary" :aria-label="t('transcoding.logLoading')"
                       class="mb-3"/>
    <div ref="viewport" class="log-viewport" role="log" aria-live="off" :aria-label="title" tabindex="0"
         @scroll="onScroll">
      <p v-if="!entries.length && !loading && !error" class="text-caption muted pa-4">{{ emptyText ||
        t('transcoding.logEmpty') }}</p>
      <ol class="log-records">
        <li v-for="entry in entries" :key="entry.id" :data-log-id="entry.id" class="log-record"
            :class="`log-record-${severity(entry.level)}`">
          <slot name="before-metadata" :entry="entry"/>
          <div class="log-metadata text-caption">
            <time :datetime="entry.timestampUtc" :title="entry.timestampUtc" class="mono muted">{{
              formatTimestamp(entry.timestampUtc) }}
            </time>
            <span :class="`text-${severity(entry.level) === 'information' ? 'medium-emphasis' : severity(entry.level)}`">{{
              levelLabel(entry.level) }}
            </span>
            <slot name="metadata" :entry="entry"/>
          </div>
          <pre class="log-message mono">{{ entry.message }}</pre>
        </li>
      </ol>
    </div>
    <p v-if="entries.length === entryLimit" class="text-caption muted mt-3">{{ t('transcoding.logLimit', {
      count: formatNumber(entryLimit) }) }}</p>
  </div>
</template>

<script setup lang="ts" generic="Entry extends { id: string | number; timestampUtc: string; level: string; message: string }">
import {computed, nextTick, onBeforeUnmount, ref, watch} from 'vue'
import {useI18n} from 'vue-i18n'
import {formatNumber} from '@/utilities/format'

const props = defineProps<{
  entries: readonly Entry[];
  loading: boolean;
  error: string;
  retrying: boolean;
  title: string;
  streamKey: string | number;
  entryLimit: number;
  labelLevel?: (level: string) => string;
  emptyText?: string;
  compact?: boolean;
  fillHeight?: boolean
}>()
const {t, locale} = useI18n()
const viewport = ref<HTMLElement>()
const following = ref(true)
const timestampFormat = computed(() => new Intl.DateTimeFormat(locale.value, {
  year: 'numeric',
  month: '2-digit',
  day: '2-digit',
  hour: '2-digit',
  minute: '2-digit',
  second: '2-digit',
  hour12: false,
}))
let scrollRevision = 0

function severity(level: string): 'error' | 'warning' | 'information' {
  if (level === 'Error' || level === 'Critical') {
    return 'error'
  }
  if (level === 'Warning') {
    return 'warning'
  }
  return 'information'
}

function levelLabel(level: string): string {
  if (props.labelLevel) {
    return props.labelLevel(level)
  }
  const keys = {error: 'logLevelError', warning: 'logLevelWarning', information: 'logLevelInformation'}
  return t(`transcoding.${keys[severity(level)]}`)
}

function formatTimestamp(value: string): string {
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? '—' : timestampFormat.value.format(date)
}

function onScroll(): void {
  const element = viewport.value
  if (element) {
    following.value = element.scrollHeight - element.clientHeight - element.scrollTop <= 8
  }
}

function returnToLatest(): void {
  following.value = true
  if (viewport.value) {
    viewport.value.scrollTop = viewport.value.scrollHeight
  }
}

watch(() => props.entries, async () => {
  const revision = ++scrollRevision
  const element = viewport.value
  if (!element) {
    return
  }
  const shouldFollow = following.value
  const top = element.getBoundingClientRect().top
  const anchor = shouldFollow ? undefined : Array.from(element.querySelectorAll<HTMLElement>('[data-log-id]'))
    .find(row => row.getBoundingClientRect().bottom > top)
  const anchorId = anchor?.dataset.logId
  const anchorOffset = anchor ? anchor.getBoundingClientRect().top - top : 0
  await nextTick()
  if (revision !== scrollRevision || !viewport.value) {
    return
  }
  if (shouldFollow && following.value) {
    returnToLatest()
  } else if (anchorId) {
    const current = Array.from(viewport.value.querySelectorAll<HTMLElement>('[data-log-id]')).find(row => row.dataset.logId === anchorId)
    if (current) {
      viewport.value.scrollTop += current.getBoundingClientRect().top - viewport.value.getBoundingClientRect().top - anchorOffset
    } else {
      viewport.value.scrollTop = 0
    }
  }
})

watch(() => props.streamKey, () => {
  scrollRevision++
  following.value = true
}, {flush: 'sync'})
onBeforeUnmount(() => {
  scrollRevision++
})
</script>

<style scoped>
.log-toolbar {
  display: flex;
  align-items: center;
  gap: 12px;
  min-height: 32px;
}

.log-viewport {
  position: relative;
  height: min(56vh, 520px);
  min-height: 180px;
  overflow-y: auto;
  overflow-anchor: none;
  border: 1px solid rgba(var(--v-theme-on-surface), .12);
  border-radius: 8px;
}

.log-entry-list-compact .log-viewport {
  height: min(40vh, 340px);
}

.log-entry-list-fill {
  display: flex;
  flex: 1 1 auto;
  flex-direction: column;
  min-height: 0;
}

.log-entry-list-fill > :not(.log-viewport) {
  flex-shrink: 0;
}

.log-entry-list-fill .log-toolbar {
  flex-wrap: wrap;
}

.log-entry-list-fill .log-viewport {
  flex: 1 1 auto;
  height: auto;
  min-height: 4rem;
}

.log-records {
  margin: 0;
  padding: 0;
  list-style: none;
}

.log-record {
  padding: 12px 14px;
  border-bottom: 1px solid rgba(var(--v-theme-on-surface), .08);
}

.log-record:last-child {
  border-bottom: 0;
}

.log-metadata {
  display: flex;
  align-items: baseline;
  flex-wrap: wrap;
  gap: 4px 12px;
}

.log-message {
  margin: 5px 0 0;
  font-size: .8125rem;
  line-height: 1.6;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

.log-record-error {
  background: rgba(var(--v-theme-error), .05);
}

.log-record-warning {
  background: rgba(var(--v-theme-warning), .05);
}
</style>
