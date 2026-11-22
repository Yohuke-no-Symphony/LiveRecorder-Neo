<template>
  <v-dialog v-model="opened" max-width="680" scrollable :aria-labelledby="titleId">
    <ContentCard :title="title" :title-id="titleId" class="ai-analysis-details">
      <v-alert v-if="error" type="error" class="mb-4">{{ error }}<v-btn variant="text" size="small" :disabled="busy || loading" @click="emit('refresh')">{{
          t('views.common.retry') }}</v-btn>
      </v-alert>
      <v-progress-linear v-if="loading" indeterminate color="primary" class="mb-4"/>
      <p v-if="status && !status.enabled" class="text-caption muted mb-4">{{ t('replay.aiDisabled') }}</p>
      <p v-if="!ended" class="text-caption muted mb-4">{{ t(isLiveMarkers ? 'liveMarkers.aiEndedOnly' :
        'replay.aiEndedOnly') }}</p>
      <template v-if="job">
        <p v-if="job.title" class="text-body-2 mb-4">{{ job.title }}</p>
        <div class="details-row mb-4">
          <span role="status">{{ stateLabel }}</span>
          <span v-if="hasLegacyProgress && job.state === 'Running' && job.phase && job.phase !== 'Publishing'"
                class="text-caption muted">{{ t(`replay.phases.${job.phase}`) }}</span>
          <span v-if="hasLegacyProgress" class="text-caption muted">{{ t('replay.progress', { value: formatNumber(progress, { maximumFractionDigits: 1 }) }) }}</span>
        </div>
        <TaskStages v-if="stages.length" :stages="stages" :task-type="taskType" :selected-id="stage?.id" expanded
                    @select="selectedStageId = $event"/>
        <TaskStageDetails v-if="stage" :stage="stage" :task-type="taskType" :error="stageError"/>
        <v-progress-linear v-else-if="hasLegacyProgress && job.state === 'Running'" :model-value="progress"
                           color="primary" class="mb-4"
                           :aria-label="t('replay.progress', { value: formatNumber(progress, { maximumFractionDigits: 1 }) })"/>
        <div v-if="stage?.id === 'Analyzing' || hasLegacyProgress" class="usage-row text-body-2"
             :class="{ 'mt-4': stage }">
          <div v-if="job.totalSegments">
            <p>{{ t('replay.segments', { completed: job.completedSegments ?? 0, total: job.totalSegments }) }}</p>
            <p v-if="job.currentSegment != null" class="text-caption muted mt-2">{{ t('replay.currentSegment', { number:
              job.currentSegment }) }}<span v-if="job.currentSegmentStartMs != null && job.currentSegmentEndMs != null"> · {{ formatDuration(job.currentSegmentStartMs) }} — {{ formatDuration(job.currentSegmentEndMs) }}</span>
            </p>
          </div>
          <div>
            <p>{{ t('replay.tokens', { used: formatNumber(job.promptTokens + job.completionTokens +
              job.estimatedTokens), budget: formatNumber(job.tokenBudget) }) }}<span v-if="job.estimatedTokens > 0"
                                                                                     class="text-caption muted"> · {{ t('replay.estimated') }}</span>
            </p>
            <div class="details-row text-caption muted mt-2">
              <span>{{ t('replay.requests', { count: job.modelRequests }) }}</span>
              <span
                v-if="job.requestRetries != null">{{ t('replay.requestRetries', { count: job.requestRetries }) }}</span>
              <span>{{ t('replay.tools', { count: job.toolCalls }) }}</span>
              <span v-if="job.contextCompactions != null">{{ t('replay.compactions', { count: job.contextCompactions }) }}</span>
              <span v-if="job.progressRecoveries != null">{{ t('replay.recoveries', { count: job.progressRecoveries }) }}</span>
            </div>
          </div>
        </div>
        <v-alert v-if="jobError" type="warning" variant="tonal" class="mt-4">{{ jobError }}</v-alert>
        <RouterLink v-if="isLiveMarkers && job.liveMarkerFileId" class="generated-markers-link text-body-2 mt-4"
                    :to="liveMarkerLocation(job.sessionId, job.liveMarkerFileId)" target="_blank"
                    rel="noopener noreferrer">{{ t('liveMarkers.viewGenerated') }} ↗</RouterLink>
      </template>
      <p v-else-if="!loading && !error" class="text-caption muted">{{ t(isLiveMarkers ? 'liveMarkers.aiEmpty' :
        'replay.aiEmpty') }}</p>
      <template #actions>
        <v-btn v-if="(canGenerate ?? status?.canGenerate) && running" :disabled="!canCancel" variant="text"
               color="error" @click="emit('cancel')">{{ t(job?.cancelRequested ? 'views.jobs.cancelling' : isLiveMarkers
          ? 'liveMarkers.cancelGeneration' : 'replay.cancel') }}</v-btn>
        <v-btn variant="text" @click="opened = false">{{ t('views.common.close') }}</v-btn>
      </template>
    </ContentCard>
  </v-dialog>
</template>

<script setup lang="ts">
import {computed, ref, useId, watch} from 'vue'
import {useI18n} from 'vue-i18n'
import {RouterLink} from 'vue-router'
import ContentCard from '@/components/common/ContentCard.vue'
import TaskStageDetails from '@/components/tasks/TaskStageDetails.vue'
import TaskStages from '@/components/tasks/TaskStages.vue'
import type {AiAnalysisJob, AiAnalysisStatus, AiJobPurpose} from '@/models/aiAnalysis'
import {liveMarkerLocation} from '@/utilities/aiJobs'
import {formatDuration, formatNumber} from '@/utilities/format'
import {taskStageError} from '@/utilities/taskHistory'

const opened = defineModel<boolean>({required: true})
const props = withDefaults(defineProps<{
  status?: AiAnalysisStatus;
  job?: AiAnalysisJob;
  purpose?: AiJobPurpose;
  canGenerate?: boolean;
  loading: boolean;
  busy: boolean;
  error: string;
  ended: boolean;
  canCancel: boolean
}>(), {canGenerate: undefined})
const emit = defineEmits<{ refresh: []; cancel: [] }>()
const {t} = useI18n()
const titleId = useId()
const job = computed(() => props.job ?? props.status?.currentJob)
const taskType = computed(() => job.value?.purpose ?? props.purpose ?? 'Analysis')
const isLiveMarkers = computed(() => taskType.value === 'LiveMarkers')
const title = computed(() => t(isLiveMarkers.value ? 'liveMarkers.generationDetails' : 'replay.details'))
const running = computed(() => job.value?.state === 'Queued' || job.value?.state === 'Running')
const progress = computed(() => Math.max(0, Math.min(100, job.value?.progress ?? 0)))
const stages = computed(() => job.value?.stages ?? [])
const hasLegacyProgress = computed(() => job.value != null && job.value.state !== 'Queued' && stages.value.length === 0)
const selectedStageId = ref<string>()
const stage = computed(() => {
  const selected = stages.value.find(item => item.id === selectedStageId.value)
    ?? stages.value.find(item => item.state === 'Running')
    ?? stages.value.find(item => item.state === 'Waiting')
  if (selected) {
    return selected
  }

  const latestFirst = [...stages.value].reverse()
  return latestFirst.find(item => ['Failed', 'Interrupted', 'Cancelled'].includes(item.state))
    ?? latestFirst.find(item => !['Pending', 'Queued', 'Skipped'].includes(item.state))
    ?? stages.value[0]
})
const stateLabel = computed(() => {
  if (job.value?.state === 'Running') {
    if (job.value.phase === 'Publishing') {
      return t('replay.phases.Publishing')
    }
    if (stages.value.length) {
      return t('taskHistory.stageStates.Running')
    }
    if (isLiveMarkers.value) {
      return t('liveMarkers.generating')
    }
  }
  return job.value ? t(`replay.states.${job.value.state}`) : ''
})
const jobError = computed(() => job.value?.error || (job.value && ['Failed', 'Interrupted'].includes(job.value.state)
  ? t(isLiveMarkers.value ? 'liveMarkers.aiFailed' : 'replay.aiFailed') : ''))
const stageError = computed(() => {
  const error = stage.value ? taskStageError(stage.value) : ''
  return error === jobError.value ? '' : error
})

watch([opened, () => job.value?.id, () => job.value?.attempt], () => {
  selectedStageId.value = undefined
}, {flush: 'sync'})
</script>

<style scoped>
.details-row {
  display: flex;
  align-items: center;
  gap: 12px;
  flex-wrap: wrap;
}

.usage-row {
  display: grid;
  gap: 12px;
}

.generated-markers-link {
  display: inline-block;
  color: rgb(var(--v-theme-primary));
}
</style>
