<template>
  <v-dialog v-model="opened" max-width="680" scrollable :aria-label="title">
    <ContentCard :title="title" class="ai-analysis-details">
      <v-alert v-if="error" type="error" class="mb-4">{{ error }}<v-btn variant="text" size="small" :disabled="busy || loading" @click="emit('refresh')">{{ t('views.common.retry') }}</v-btn></v-alert>
      <v-progress-linear v-if="loading" indeterminate color="primary" class="mb-4" />
      <p v-if="status && !status.enabled" class="text-caption muted mb-4">{{ t('replay.aiDisabled') }}</p>
      <p v-if="!ended" class="text-caption muted mb-4">{{ t(isLiveMarkers ? 'liveMarkers.aiEndedOnly' : 'replay.aiEndedOnly') }}</p>
      <template v-if="job">
        <p v-if="job.title" class="text-body-2 mb-4">{{ job.title }}</p>
        <div class="details-row mb-4">
          <span role="status">{{ t(isLiveMarkers && job.state === 'Running' ? 'liveMarkers.generating' : `replay.states.${job.state}`) }}</span>
          <span v-if="job.state === 'Running' && job.phase" class="text-caption muted">{{ t(`replay.phases.${job.phase}`) }}</span>
          <span class="text-caption muted">{{ t('replay.progress', { value: formatNumber(progress, { maximumFractionDigits: 1 }) }) }}</span>
        </div>
        <v-progress-linear v-if="running" :model-value="progress" :indeterminate="job.state === 'Queued'" color="primary" class="mb-4" />
        <div v-if="job.totalSegments" class="segment-progress text-body-2 mb-4">
          <p>{{ t('replay.segments', { completed: job.completedSegments ?? 0, total: job.totalSegments }) }}</p>
          <p v-if="job.currentSegment != null" class="text-caption muted mt-2">{{ t('replay.currentSegment', { number: job.currentSegment }) }}<span v-if="job.currentSegmentStartMs != null && job.currentSegmentEndMs != null"> · {{ formatDuration(job.currentSegmentStartMs) }} — {{ formatDuration(job.currentSegmentEndMs) }}</span></p>
        </div>
        <div class="usage-row text-body-2">
          <p>{{ t('replay.tokens', { used: formatNumber(job.promptTokens + job.completionTokens + job.estimatedTokens), budget: formatNumber(job.tokenBudget) }) }}<span v-if="job.estimatedTokens > 0" class="text-caption muted"> · {{ t('replay.estimated') }}</span></p>
          <div class="details-row text-caption muted">
            <span>{{ t('replay.requests', { count: job.modelRequests }) }}</span>
            <span v-if="job.requestRetries != null">{{ t('replay.requestRetries', { count: job.requestRetries }) }}</span>
            <span>{{ t('replay.tools', { count: job.toolCalls }) }}</span>
            <span v-if="job.contextCompactions != null">{{ t('replay.compactions', { count: job.contextCompactions }) }}</span>
            <span v-if="job.progressRecoveries != null">{{ t('replay.recoveries', { count: job.progressRecoveries }) }}</span>
          </div>
        </div>
        <v-alert v-if="job.error || ['Failed', 'Interrupted'].includes(job.state)" type="warning" variant="tonal" class="mt-4">{{ job.error || t(isLiveMarkers ? 'liveMarkers.aiFailed' : 'replay.aiFailed') }}</v-alert>
        <RouterLink v-if="isLiveMarkers && job.liveMarkerFileId" class="generated-markers-link text-body-2 mt-4" :to="liveMarkerLocation(job.sessionId, job.liveMarkerFileId)" target="_blank" rel="noopener noreferrer">{{ t('liveMarkers.viewGenerated') }} ↗</RouterLink>
      </template>
      <p v-else-if="!loading && !error" class="text-caption muted">{{ t(isLiveMarkers ? 'liveMarkers.aiEmpty' : 'replay.aiEmpty') }}</p>
      <template #actions>
        <v-btn v-if="(canGenerate ?? status?.canGenerate) && running" :disabled="!canCancel" variant="text" color="error" @click="emit('cancel')">{{ t(job?.cancelRequested ? 'views.jobs.cancelling' : isLiveMarkers ? 'liveMarkers.cancelGeneration' : 'replay.cancel') }}</v-btn>
        <v-btn variant="text" @click="opened = false">{{ t('views.common.close') }}</v-btn>
      </template>
    </ContentCard>
  </v-dialog>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { RouterLink } from 'vue-router'
import ContentCard from '@/components/common/ContentCard.vue'
import type { AiAnalysisJob, AiAnalysisStatus, AiJobPurpose } from '@/models/aiAnalysis'
import { liveMarkerLocation } from '@/utilities/aiJobs'
import { formatDuration, formatNumber } from '@/utilities/format'

const opened = defineModel<boolean>({ required: true })
const props = withDefaults(defineProps<{ status?: AiAnalysisStatus; job?: AiAnalysisJob; purpose?: AiJobPurpose; canGenerate?: boolean; loading: boolean; busy: boolean; error: string; ended: boolean; canCancel: boolean }>(), { canGenerate: undefined })
const emit = defineEmits<{ refresh: []; cancel: [] }>()
const { t } = useI18n()
const job = computed(() => props.job ?? props.status?.currentJob)
const isLiveMarkers = computed(() => (job.value?.purpose ?? props.purpose) === 'LiveMarkers')
const title = computed(() => t(isLiveMarkers.value ? 'liveMarkers.generationDetails' : 'replay.details'))
const running = computed(() => job.value?.state === 'Queued' || job.value?.state === 'Running')
const progress = computed(() => Math.max(0, Math.min(100, job.value?.progress ?? 0)))
</script>

<style scoped>
.details-row { display: flex; align-items: center; gap: 12px; flex-wrap: wrap; }
.usage-row { display: grid; gap: 12px; }
.generated-markers-link { display: inline-block; color: rgb(var(--v-theme-primary)); }
</style>
