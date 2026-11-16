<template>
  <PageHeader :title="t('views.common.jobs')">
    <v-tooltip :text="t('views.common.refresh')" location="bottom">
      <template #activator="{ props: tooltipProps }">
        <v-btn v-bind="tooltipProps" class="job-action-button" icon="mdi-refresh" variant="text" size="small" :aria-label="t('views.common.refresh')" :loading="busy" :disabled="busy" @click="refresh" />
      </template>
    </v-tooltip>
  </PageHeader>
  <v-alert v-if="error" type="error" class="mb-6">{{ error }}</v-alert>
  <v-card v-if="jobs.length" class="jobs-card">
    <div class="table-toolbar"><v-chip-group v-model="filter" mandatory color="primary"><v-chip value="all">{{ t('views.common.all') }}</v-chip><v-chip value="active">{{ t('views.jobs.active') }}</v-chip><v-chip value="failed">{{ t('views.jobs.failed') }}</v-chip></v-chip-group><span class="text-caption muted">{{ t('views.jobs.count', { count: formatNumber(visibleJobs.length) }) }}</span></div>
    <v-table class="jobs-table">
      <thead><tr><th class="job-task">{{ t('views.jobs.task') }}</th><th class="job-status">{{ t('views.common.status') }}</th><th class="job-progress">{{ t('views.jobs.progress') }}</th><th class="job-created">{{ t('views.common.createdAt') }}</th><th class="job-actions" /></tr></thead>
      <tbody>
        <tr v-for="entry in visibleJobs" :key="jobKey(entry)" :data-job-kind="entry.kind" :data-job-id="entry.job.id">
          <td class="job-task py-4">
            <template v-if="entry.kind === 'ai'">
              <a :href="recordingUrl(entry.job.sessionId)" target="_blank" rel="noopener noreferrer" class="job-title font-weight-medium">{{ entry.job.title || t('components.library.unnamedSession') }}</a>
              <div class="job-type text-caption muted mt-1">{{ t(entry.job.purpose === 'LiveMarkers' ? 'liveMarkers.generate' : 'replay.taskType') }}</div>
            </template>
            <template v-else-if="entry.kind === 'transcode'">
              <a :href="recordingUrl(entry.job.sessionId)" target="_blank" rel="noopener noreferrer" class="job-title font-weight-medium">{{ entry.job.title || t('components.library.unnamedSession') }}</a>
              <div class="text-caption muted mt-1">{{ t('transcoding.title') }} · {{ entry.job.profileName }}<template v-if="entry.job.stage"> · {{ transcodeStage(entry.job.stage) }}</template></div>
              <div v-if="entry.job.workerId" class="text-caption muted">{{ t('transcoding.worker') }}: {{ entry.job.workerId }}</div>
              <div v-for="(detail, index) in transcodeDetails(entry.job)" :key="index" class="job-activity text-caption muted mt-1">{{ detail }}</div>
              <div v-if="entry.job.error" class="text-caption text-error mt-2">{{ entry.job.error }}</div>
            </template>
            <template v-else>
              <router-link :to="`/sessions/${entry.job.sessionId}`" class="job-title font-weight-medium">{{ entry.job.title || kindLabels[entry.job.kind] || entry.job.kind }}</router-link>
              <div class="job-type text-caption muted mt-1">{{ kindLabels[entry.job.kind] || entry.job.kind }}<span v-if="entry.job.kind === 'Clip'"> · {{ entry.job.mode === 'Fast' ? t('views.jobs.fast') : t('views.jobs.exact') }}</span></div>
              <div v-if="entry.job.error" class="text-caption text-error mt-2">{{ entry.job.error }}</div>
            </template>
          </td>
          <td class="job-status"><StatusChip :status="entry.job.state" /></td>
          <td class="job-progress">
            <v-progress-linear :model-value="progress(entry.job)" :indeterminate="['Running', 'Publishing'].includes(entry.job.state) && entry.job.progress === 0" color="primary" height="4" rounded />
            <div class="text-caption mono muted mt-2">{{ entry.kind === 'transcode' ? t('transcoding.progressOverall', { value: formatNumber(progress(entry.job)) }) : `${progress(entry.job)}%` }}</div>
            <div v-if="entry.kind === 'transcode' && stageProgress(entry.job) !== undefined" class="text-caption mono muted mt-1">{{ t('transcoding.progressStage') }} {{ formatNumber(stageProgress(entry.job)!, { maximumFractionDigits: 1 }) }}%</div>
          </td>
          <td class="job-created text-caption muted text-no-wrap">{{ formatDate(entry.job.createdAtUtc) }}</td>
          <td class="job-actions">
            <div class="action-buttons">
              <template v-if="entry.kind === 'ai'">
                <v-tooltip :text="t(entry.job.purpose === 'LiveMarkers' ? 'liveMarkers.generationDetails' : 'replay.details')" location="bottom">
                  <template #activator="{ props: tooltipProps }">
                    <v-btn v-bind="tooltipProps" class="job-action-button" icon="mdi-information-outline" variant="text" size="small" :aria-label="t(entry.job.purpose === 'LiveMarkers' ? 'liveMarkers.generationDetails' : 'replay.details')" @click="selectedAiId = entry.job.id" />
                  </template>
                </v-tooltip>
                <v-tooltip :text="t(entry.job.purpose === 'LiveMarkers' ? 'liveMarkers.viewGenerated' : 'views.jobs.analysisResults')" location="bottom">
                  <template #activator="{ props: tooltipProps }">
                    <v-btn v-bind="tooltipProps" class="job-action-button" :icon="entry.job.purpose === 'LiveMarkers' ? 'mdi-format-list-bulleted' : 'mdi-chart-box-outline'" :href="router.resolve(aiJobResultLocation(entry.job)).href" target="_blank" rel="noopener noreferrer" variant="text" size="small" color="primary" :aria-label="t(entry.job.purpose === 'LiveMarkers' ? 'liveMarkers.viewGenerated' : 'views.jobs.analysisResults')" />
                  </template>
                </v-tooltip>
                <v-tooltip v-if="canGenerateAiJob(entry.job) && isActive(entry.job.state)" :text="entry.job.cancelRequested ? t('views.jobs.cancelling') : t('views.common.cancel')" location="bottom">
                  <template #activator="{ props: tooltipProps }">
                    <v-btn v-bind="tooltipProps" class="job-action-button" icon="mdi-close" variant="text" size="small" :aria-label="entry.job.cancelRequested ? t('views.jobs.cancelling') : t('views.common.cancel')" :disabled="entry.job.cancelRequested || entry.job.phase === 'Publishing' || Boolean(actionKey)" :loading="actionKey === jobKey(entry)" @click="act(entry, 'cancel')" />
                  </template>
                </v-tooltip>
              </template>
              <template v-else-if="entry.kind === 'transcode'">
                <v-tooltip v-if="auth.isAdmin && isActive(entry.job.state)" :text="entry.job.cancelRequested ? t('views.jobs.cancelling') : t('views.common.cancel')" location="bottom">
                  <template #activator="{ props: tooltipProps }">
                    <v-btn v-bind="tooltipProps" class="job-action-button" icon="mdi-close" variant="text" size="small" :aria-label="entry.job.cancelRequested ? t('views.jobs.cancelling') : t('views.common.cancel')" :disabled="entry.job.cancelRequested || Boolean(actionKey)" :loading="actionKey === jobKey(entry)" @click="act(entry, 'cancel')" />
                  </template>
                </v-tooltip>
                <v-tooltip v-if="auth.isAdmin && ['Failed', 'Cancelled', 'Skipped'].includes(entry.job.state)" :text="t('views.common.retry')" location="bottom">
                  <template #activator="{ props: tooltipProps }">
                    <v-btn v-bind="tooltipProps" class="job-action-button" icon="mdi-refresh" variant="text" size="small" color="primary" :aria-label="t('views.common.retry')" :disabled="Boolean(actionKey)" :loading="actionKey === jobKey(entry)" @click="act(entry, 'retry')" />
                  </template>
                </v-tooltip>
              </template>
              <template v-else>
                <v-tooltip v-if="isActive(entry.job.state)" :text="entry.job.cancelRequested ? t('views.jobs.cancelling') : t('views.common.cancel')" location="bottom">
                  <template #activator="{ props: tooltipProps }">
                    <v-btn v-bind="tooltipProps" class="job-action-button" icon="mdi-close" variant="text" size="small" :aria-label="entry.job.cancelRequested ? t('views.jobs.cancelling') : t('views.common.cancel')" :disabled="entry.job.cancelRequested || Boolean(actionKey)" :loading="actionKey === jobKey(entry)" @click="act(entry, 'cancel')" />
                  </template>
                </v-tooltip>
                <v-tooltip v-if="['Failed', 'Canceled', 'Cancelled', 'Interrupted'].includes(entry.job.state) && (entry.job.kind === 'Clip' ? auth.canCreateClips : auth.isAdmin)" :text="t('views.common.retry')" location="bottom">
                  <template #activator="{ props: tooltipProps }">
                    <v-btn v-bind="tooltipProps" class="job-action-button" icon="mdi-refresh" variant="text" size="small" color="primary" :aria-label="t('views.common.retry')" :disabled="Boolean(actionKey)" :loading="actionKey === jobKey(entry)" @click="act(entry, 'retry')" />
                  </template>
                </v-tooltip>
                <template v-if="auth.canDownloadRecordings && entry.job.state === 'Completed' && entry.job.kind === 'Merge'">
                  <v-tooltip v-for="(_, index) in entry.job.outputRelativePaths" :key="index" :text="entry.job.outputRelativePaths.length > 1 ? t('views.jobs.downloadNumberedFile', { number: formatNumber(index + 1) }) : t('views.jobs.downloadFile')" location="bottom">
                    <template #activator="{ props: tooltipProps }">
                      <v-btn v-bind="tooltipProps" class="job-action-button" icon="mdi-download" :href="MediaAPI.downloadJobOutput(entry.job.id, index)" variant="text" size="small" color="primary" :aria-label="entry.job.outputRelativePaths.length > 1 ? t('views.jobs.downloadNumberedFile', { number: formatNumber(index + 1) }) : t('views.jobs.downloadFile')" />
                    </template>
                  </v-tooltip>
                </template>
              </template>
            </div>
          </td>
        </tr>
      </tbody>
    </v-table>
  </v-card>
  <EmptyState v-else-if="!busy" :title="t('views.jobs.empty')" icon="mdi-progress-clock" />
  <AiAnalysisDetailsDialog v-if="selectedAiJob" v-model="detailsOpen" :job="selectedAiJob" :can-generate="canGenerateAiJob(selectedAiJob)" :can-cancel="canCancelSelected" :loading="false" :busy="Boolean(actionKey)" :error="detailsError" :ended="true" @refresh="refresh" @cancel="cancelSelected" />
</template>

<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import { MediaAPI } from '@/apis/MediaAPI'
import { useAuthStore } from '@/stores/auth'
import { useBackgroundJobs } from '@/composables/useBackgroundJobs'
import type { BackgroundJob } from '@/models/backgroundJob'
import type { TranscodeJob } from '@/models/transcoding'
import { aiJobResultLocation } from '@/utilities/aiJobs'
import { formatBytes, formatDate, formatDuration, formatNumber } from '@/utilities/format'
import PageHeader from '@/components/common/PageHeader.vue'
import EmptyState from '@/components/common/EmptyState.vue'
import StatusChip from '@/components/common/StatusChip.vue'
import AiAnalysisDetailsDialog from '@/components/analytics/AiAnalysisDetailsDialog.vue'

const { t, locale } = useI18n()
const auth = useAuthStore()
const router = useRouter()
const { jobs, aiJobs, loading, actionKey, error, actionError, actionErrorKey, canGenerateAiJob, refresh, act } = useBackgroundJobs()
const filter = ref('all')
const selectedAiId = ref('')
const busy = computed(() => loading.value || Boolean(actionKey.value))
const kindLabels = computed<Record<string, string>>(() => ({ Clip: t('views.common.clips'), Repair: t('views.session.repair'), Merge: t('views.session.merge') }))
const visibleJobs = computed(() => jobs.value.filter(entry => filter.value === 'all' || (filter.value === 'active'
  ? isActive(entry.job.state)
  : ['Failed', 'Partial', 'Interrupted', 'Canceled', 'Cancelled'].includes(entry.job.state))))
const selectedAiJob = computed(() => aiJobs.value.find(job => job.id === selectedAiId.value))
const detailsOpen = computed({ get: () => Boolean(selectedAiJob.value), set: value => { if (!value) { selectedAiId.value = '' } } })
const detailsError = computed(() => actionErrorKey.value === `ai:${selectedAiId.value}` ? actionError.value : '')
const canCancelSelected = computed(() => Boolean(selectedAiJob.value && canGenerateAiJob(selectedAiJob.value) && isActive(selectedAiJob.value.state) && selectedAiJob.value.phase !== 'Publishing' && !selectedAiJob.value.cancelRequested && !actionKey.value))

function transcodeStage(stage: string): string
{
  if (stage === 'WaitingForWorker') { return t('transcoding.waitingWorker') }
  if (stage === 'Skipped') { return t('transcoding.skipped') }
  const states: Record<string, string> = { Completed: 'completed', Cancelled: 'canceled', Failed: 'failed', Running: 'running', SourceUnavailable: 'unavailable' }
  if (states[stage]) { return t(`components.status.${states[stage]}`) }
  return ['Downloading', 'Inspecting', 'WaitingForGpu', 'Encoding', 'Uploading', 'Validating', 'Finalizing', 'Publishing'].includes(stage) ? t(`transcoding.stage${stage}`) : stage
}

function hasMetric(value: number | null | undefined): value is number
{
  return typeof value === 'number' && Number.isFinite(value) && value >= 0
}

function stageProgress(job: TranscodeJob): number | undefined
{
  const activity = job.activity
  if (!activity) { return undefined }
  const completed = job.stage === 'Encoding' ? activity.processedDurationMs : ['Downloading', 'Uploading'].includes(job.stage) ? activity.bytesTransferred : undefined
  const total = job.stage === 'Encoding' ? activity.totalDurationMs : ['Downloading', 'Uploading'].includes(job.stage) ? activity.totalBytes : undefined
  return hasMetric(completed) && hasMetric(total) && total > 0 ? Math.min(100, completed / total * 100) : undefined
}

function transcodeDetails(job: TranscodeJob): string[]
{
  const details: string[] = []
  const activity = job.activity
  const metrics: string[] = []
  if (activity && ['Downloading', 'Uploading'].includes(job.stage))
  {
    if (hasMetric(activity.bytesTransferred) && hasMetric(activity.totalBytes))
    {
      metrics.push(t('transcoding.progressTransferred', { completed: formatBytes(activity.bytesTransferred), total: formatBytes(activity.totalBytes) }))
    }
    if (isActive(job.state) && hasMetric(activity.bytesPerSecond)) { metrics.push(`${formatBytes(activity.bytesPerSecond)}/s`) }
  }
  if (activity && job.stage === 'Encoding')
  {
    if (hasMetric(activity.processedDurationMs) && hasMetric(activity.totalDurationMs))
    {
      metrics.push(t('transcoding.progressMedia', { completed: formatDuration(activity.processedDurationMs, true), total: formatDuration(activity.totalDurationMs, true) }))
    }
    if (isActive(job.state) && hasMetric(activity.framesPerSecond)) { metrics.push(t('transcoding.progressFramesPerSecond', { value: formatNumber(activity.framesPerSecond, { maximumFractionDigits: 1 }) })) }
    if (isActive(job.state) && hasMetric(activity.speed)) { metrics.push(t('transcoding.progressSpeed', { value: formatNumber(activity.speed, { maximumFractionDigits: 2 }) })) }
  }
  if (activity && isActive(job.state) && ['Downloading', 'Encoding', 'Uploading'].includes(job.stage) && hasMetric(activity.estimatedSecondsRemaining))
  {
    metrics.push(t('transcoding.progressRemaining', { time: formatDuration(activity.estimatedSecondsRemaining * 1000, true) }))
  }
  if (metrics.length) { details.push(metrics.join(' · ')) }
  if (activity && ['Downloading', 'Uploading'].includes(job.stage))
  {
    const files: string[] = []
    if (hasMetric(activity.filesCompleted) && hasMetric(activity.filesTotal))
    {
      files.push(t('transcoding.progressFiles', { completed: formatNumber(activity.filesCompleted), total: formatNumber(activity.filesTotal) }))
    }
    if (activity.currentFile) { files.push(activity.currentFile) }
    if (files.length) { details.push(files.join(' · ')) }
  }
  if (job.encoder) { details.push(t('transcoding.progressEncoder', { encoder: job.encoder })) }
  if (job.lastProgressAtUtc)
  {
    const time = new Date(job.lastProgressAtUtc)
    if (Number.isFinite(time.getTime()))
    {
      details.push(t('transcoding.progressUpdated', { time: new Intl.DateTimeFormat(locale.value, { dateStyle: 'short', timeStyle: 'medium', hour12: false }).format(time) }))
    }
  }
  return details
}
function isActive(state: string): boolean
{
  return ['Queued', 'Running', 'Publishing'].includes(state)
}

function jobKey(entry: BackgroundJob): string
{
  return `${entry.kind}:${entry.job.id}`
}

function progress(job: { state: string; progress: number }): number
{
  return ['Completed', 'Succeeded'].includes(job.state) ? 100 : Math.round(Math.max(0, Math.min(100, job.progress)))
}

function recordingUrl(id: string): string
{
  return router.resolve({ path: `/sessions/${encodeURIComponent(id)}` }).href
}

function cancelSelected(): void
{
  if (selectedAiJob.value && canCancelSelected.value)
  {
    void act({ kind: 'ai', job: selectedAiJob.value }, 'cancel')
  }
}

watch(selectedAiJob, value =>
{
  if (!value)
  {
    selectedAiId.value = ''
  }
}, { flush: 'sync' })
</script>

<style scoped>
.jobs-card { min-width: 0; max-width: 100%; }
.jobs-table :deep(.v-table__wrapper) { overflow-x: auto; }
.jobs-table :deep(table) { min-width: 920px; }
.job-task { min-width: 220px; width: 28%; }
.job-title { display: block; max-width: 360px; white-space: normal; overflow-wrap: anywhere; }
.job-type, .job-status { white-space: nowrap; }
.job-activity { max-width: 460px; overflow-wrap: anywhere; font-variant-numeric: tabular-nums; }
.job-status { min-width: 110px; }
.job-progress { min-width: 150px; }
.job-created { min-width: 180px; }
.job-actions { min-width: 104px; }
.job-actions .action-buttons { flex-wrap: nowrap; }
.job-action-button { flex: 0 0 32px; width: 32px; min-width: 32px; height: 32px; padding: 0; }
.job-action-button:disabled { pointer-events: auto; }
</style>
