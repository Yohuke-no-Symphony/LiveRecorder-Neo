<template>
  <LogEntryList :entries="entries" :loading="loading" :error="error" :retrying="retrying"
                :title="title" :stream-key="streamKey" :entry-limit="MAX_WORKER_LOG_ENTRIES"
                :empty-text="emptyText" :compact="compact">
    <template #before-metadata="{ entry }">
      <p v-if="entry.droppedCount && entry.droppedCount > 0" class="text-caption text-warning mb-2" role="status">{{
        t('transcoding.logDropped', { count: formatNumber(entry.droppedCount) }) }}</p>
    </template>
    <template #metadata="{ entry }">
      <span v-if="entry.source && te(`taskHistory.logSources.${entry.source}`)" class="muted">{{
        t(`taskHistory.logSources.${entry.source}`) }}</span>
      <span v-if="showJob && entry.jobId" class="log-job muted">{{ t('transcoding.logJob') }} <code>{{ entry.jobId }}</code><template
        v-if="entry.attempt != null"> · {{ t('transcoding.logAttempt', { number: formatNumber(entry.attempt) }) }}</template></span>
    </template>
  </LogEntryList>
</template>

<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { MAX_WORKER_LOG_ENTRIES } from '@/composables/useWorkerLogs'
import type { WorkerLogEntry } from '@/models/transcoding'
import { formatNumber } from '@/utilities/format'
import LogEntryList from './LogEntryList.vue'

withDefaults(defineProps<{
  entries: WorkerLogEntry[];
  loading: boolean;
  error: string;
  retrying: boolean;
  title: string;
  streamKey: string;
  showJob?: boolean;
  emptyText?: string;
  compact?: boolean
}>(), { showJob: true })

const { t, te } = useI18n()
</script>

<style scoped>
.log-job {
  overflow-wrap: anywhere;
}
</style>
