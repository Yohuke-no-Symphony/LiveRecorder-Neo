<template>
  <ContentCard :title="t(titleKey)">
    <template #header-actions>
      <v-tooltip :text="t('transcoding.addWorker')"><template #activator="{ props: tooltipProps }"><v-btn v-bind="tooltipProps" icon="mdi-plus" variant="text" :disabled="busy" :aria-label="t('transcoding.addWorker')" @click="openCreate" /></template></v-tooltip>
      <v-btn icon="mdi-refresh" variant="text" :loading="loading" :aria-label="t('transcoding.refresh')" @click="refresh" />
    </template>
    <v-alert v-if="loadError || (error && !createOpen)" type="error" class="mb-4">{{ loadError || error }}</v-alert>
    <p v-if="!workers.length && !loading" class="muted">{{ t('transcoding.noWorkers') }}</p>
    <div class="worker-list">
      <article v-for="worker in workers" :key="worker.id" class="worker-row">
        <div class="d-flex align-center flex-wrap ga-3"><strong>{{ worker.name }}</strong><StatusChip :status="worker.enabled ? (worker.isOnline === true ? 'online' : 'disconnected') : 'disabled'" /><span class="text-caption muted">{{ worker.capabilities?.operatingSystem }} {{ worker.capabilities?.architecture }}</span><v-spacer /><v-btn size="small" variant="text" @click="logWorker = worker">{{ t('transcoding.viewLogs') }}</v-btn><v-btn size="small" variant="text" :disabled="busy" @click="edit(worker)">{{ t('transcoding.editWorker') }}</v-btn><v-btn size="small" variant="text" :disabled="busy" @click="rotateTarget = worker">{{ t('transcoding.rotateKey') }}</v-btn><v-btn icon="mdi-delete-outline" size="small" variant="text" color="error" :disabled="busy" :aria-label="t('transcoding.deleteWorker')" @click="deleteTarget = worker" /></div>
        <div class="text-caption muted mt-2">{{ t('transcoding.workerId') }}: {{ worker.id }} · {{ t('transcoding.lastSeen') }}: {{ worker.lastSeenAtUtc ? formatDate(worker.lastSeenAtUtc) : '—' }}</div>
        <div v-if="worker.telemetry" class="text-caption mt-2">
          <template v-if="isRecording">{{ t('transcoding.activeRecordings', { count: worker.telemetry.activeRecordings ?? worker.telemetry.runningJobs ?? worker.telemetry.runningCpu }) }} · {{ t('transcoding.pendingUpload', { bytes: formatBytes(worker.telemetry.pendingUploadBytes ?? -1) }) }}</template>
          <template v-else-if="isClipping">{{ t('transcoding.runningJobs', { count: worker.telemetry.runningJobs ?? worker.telemetry.runningCpu + worker.telemetry.runningGpu }) }}</template>
          <template v-else>CPU {{ worker.telemetry.cpuPercent === null ? '—' : `${Math.round(worker.telemetry.cpuPercent)}%` }} · GPU {{ worker.telemetry.runningGpu }} · CPU {{ worker.telemetry.runningCpu }}</template>
          · {{ formatBytes(worker.telemetry.freeBytes) }}
        </div>
        <div v-if="isRecording && worker.telemetry" class="text-caption mt-2">{{ t('transcoding.spoolUsage', { used: formatBytes(worker.telemetry.spoolBytes ?? -1), limit: formatBytes(worker.configuration.maxCacheBytes) }) }}</div>
        <div v-if="isTranscoding && worker.capabilities?.encoders.length" class="d-flex flex-wrap ga-1 mt-2"><v-chip v-for="encoder in worker.capabilities.encoders" :key="`${encoder.encoder}:${encoder.deviceId}`" size="x-small" :color="encoder.available ? 'success' : 'warning'" :title="encoder.error || undefined">{{ encoder.encoder }}<template v-if="encoder.deviceId"> · {{ encoder.deviceId }}</template></v-chip></div>
        <v-alert v-if="worker.configurationError" type="error" density="compact" class="mt-3">{{ worker.configurationError }}</v-alert>
        <p v-else-if="worker.configurationRevision !== worker.appliedConfigurationRevision" class="text-caption text-warning mt-2">{{ t('transcoding.configurationPending') }}</p>
        <p v-if="isTranscoding && worker.telemetry?.cpuUnavailableReason" class="text-caption muted mt-2">{{ worker.telemetry.cpuUnavailableReason }}</p>
      </article>
    </div>
    <v-dialog :model-value="createOpen" max-width="480" :persistent="busy" :aria-label="t('transcoding.addWorker')" @update:model-value="!$event && !busy && (createOpen = false)">
      <ContentCard :title="t('transcoding.addWorker')">
        <form :id="createFormId" class="form-stack" @submit.prevent="create">
          <v-alert v-if="error" type="error">{{ error }}</v-alert>
          <v-text-field v-model="newName" maxlength="100" :label="t('transcoding.workerName')" :disabled="busy" autofocus required />
        </form>
        <template #actions><v-btn variant="text" :disabled="busy" @click="createOpen = false">{{ t('transcoding.cancel') }}</v-btn><v-btn type="submit" :form="createFormId" color="primary" :disabled="!newName.trim() || newName.length > 100" :loading="busy">{{ t('transcoding.addWorker') }}</v-btn></template>
      </ContentCard>
    </v-dialog>
    <v-dialog :model-value="!!draft" :max-width="isTranscoding ? 900 : 650" :persistent="busy" scrollable :aria-label="t('transcoding.workerSettings')" @update:model-value="!$event && !busy && (draft = undefined)">
      <ContentCard v-if="draft" :title="t('transcoding.workerSettings')">
        <form :id="editFormId" class="form-stack" @submit.prevent="save">
          <v-alert v-if="editError" type="error">{{ editError }}</v-alert>
          <div class="worker-fields"><v-text-field v-model="draft.name" maxlength="100" :label="t('transcoding.workerName')" :disabled="busy" required /><v-switch v-model="draft.enabled" :label="t('transcoding.workerEnabled')" :hint="isRecording ? t('transcoding.recordingEnabledHint') : undefined" :persistent-hint="isRecording" :hide-details="isRecording ? 'auto' : true" :disabled="busy" /></div>
          <div class="worker-fields">
            <template v-if="mediaConfiguration"><v-text-field v-model="mediaConfiguration.ffmpegPath" :label="t('shell.settings.ffmpeg')" :disabled="busy" required /><v-text-field v-model="mediaConfiguration.ffprobePath" :label="t('shell.settings.ffprobe')" :disabled="busy" required /></template>
            <v-text-field v-model="draft.configuration.tempDirectory" :label="t(isRecording ? 'transcoding.recordingSpoolDirectory' : 'transcoding.tempDirectory')" :disabled="busy" />
            <template v-if="transcodingConfiguration"><v-select :items="['auto', 'h264_qsv', 'h264_nvenc', 'h264_amf', 'h264_vaapi', 'h264_videotoolbox']" v-model="transcodingConfiguration.preferredEncoder" :label="t('transcoding.preferredEncoder')" :disabled="busy" /><v-text-field v-model="transcodingConfiguration.deviceId" :label="t('transcoding.deviceId')" clearable :disabled="busy" /></template>
          </div>
          <template v-if="transcodingConfiguration"><h3 class="section-heading">{{ t('transcoding.scheduling') }}</h3><v-switch :model-value="transcodingConfiguration.cpuFallbackMode === 'Balanced'" :label="t('transcoding.cpuFallback')" :hint="t('transcoding.cpuFallbackHint')" persistent-hint :disabled="busy" @update:model-value="transcodingConfiguration.cpuFallbackMode = $event ? 'Balanced' : 'Disabled'" /></template>
          <div class="worker-fields"><v-text-field v-for="field in numericFields" :key="field.key" :model-value="numberValue(field.key, field.unit ?? 1)" :label="t(`transcoding.${field.label}`)" type="number" :min="field.min" :max="field.max" :step="field.step ?? 1" :disabled="busy" @update:model-value="setNumber(field.key, $event, field.unit ?? 1)" /></div>
        </form>
        <template #actions><v-btn variant="text" :disabled="busy" @click="draft = undefined">{{ t('transcoding.cancel') }}</v-btn><v-btn type="submit" :form="editFormId" color="primary" :loading="busy">{{ t('transcoding.saveWorker') }}</v-btn></template>
      </ContentCard>
    </v-dialog>
    <v-dialog :model-value="!!secret" max-width="650" @update:model-value="!$event && (secret = undefined)"><ContentCard v-if="secret" :title="t('transcoding.token')"><v-alert type="info" class="mb-4">{{ t('transcoding.keyCreated') }}</v-alert><v-text-field :model-value="secret.workerId" :label="t('transcoding.workerId')" readonly /><v-textarea :model-value="secret.value" :label="t('transcoding.token')" readonly rows="3" /><template #actions><v-btn color="primary" @click="secret = undefined">{{ t('transcoding.close') }}</v-btn></template></ContentCard></v-dialog>
    <ConfirmDialog :model-value="!!deleteTarget" :title="t('transcoding.deleteWorker')" :message="t(isRecording ? 'transcoding.deleteRecordingWorkerConfirm' : 'transcoding.deleteWorkerConfirm')" :action-label="t('transcoding.delete')" danger :busy="busy" :error="error" @update:model-value="!$event && !busy && (deleteTarget = undefined)" @confirm="remove" />
    <ConfirmDialog :model-value="!!rotateTarget" :title="t('transcoding.rotateKey')" :message="t('transcoding.rotateKeyConfirm')" :action-label="t('transcoding.rotateKey')" :busy="busy" :error="error" @update:model-value="!$event && !busy && (rotateTarget = undefined)" @confirm="rotate" />
    <TranscodingLogDialog v-if="logWorker" :worker="logWorker" @close="logWorker = undefined" />
  </ContentCard>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, ref, useId } from 'vue'
import { useI18n } from 'vue-i18n'
import { bandwidthInputBytes } from '@/models/bandwidth'
import { TranscodingAPI } from '@/apis/TranscodingAPI'
import { usePolling } from '@/composables/usePolling'
import { isValidWorkerConfiguration, normalizeWorker, type ClippingWorkerConfiguration, type RecordingWorkerConfiguration, type TranscodingWorkerConfiguration, type Worker, type WorkerKind } from '@/models/transcoding'
import { formatBytes, formatDate } from '@/utilities/format'
import ContentCard from './ContentCard.vue'
import ConfirmDialog from './ConfirmDialog.vue'
import StatusChip from './StatusChip.vue'
import TranscodingLogDialog from './TranscodingLogDialog.vue'

type NumericConfigurationKey<T> = { [K in keyof T]: T[K] extends number ? K : never }[keyof T]
type NumericKey = NumericConfigurationKey<TranscodingWorkerConfiguration> | NumericConfigurationKey<ClippingWorkerConfiguration> | NumericConfigurationKey<RecordingWorkerConfiguration>
const configurationFields: { key: NumericKey; label: string; min: number; max?: number; step?: number | 'any'; unit?: number; kinds?: WorkerKind[] }[] = [
  { key: 'maxConcurrentEncodes', label: 'totalConcurrency', min: 1, max: 64 }, { key: 'gpuConcurrency', label: 'gpuConcurrency', min: 0, max: 64, kinds: ['Transcoding'] }, { key: 'cpuConcurrency', label: 'cpuConcurrency', min: 0, max: 64, kinds: ['Transcoding'] },
  { key: 'clipEncodingThreads', label: 'clipEncodingThreads', min: 1, max: 128, kinds: ['Clipping'] },
  { key: 'gpuWaitSeconds', label: 'cpuIdleSeconds', min: 0, max: 86400, kinds: ['Transcoding'] }, { key: 'cpuSampleWindowSeconds', label: 'cpuAverageSeconds', min: 5, max: 300, kinds: ['Transcoding'] }, { key: 'cpuLoadThreshold', label: 'cpuThreshold', min: 1, max: 100, step: 'any', kinds: ['Transcoding'] }, { key: 'cpuThreadFraction', label: 'cpuThreadFraction', min: .01, max: 1, step: 'any', kinds: ['Transcoding'] },
  { key: 'maxCacheBytes', label: 'cacheGiB', min: 1 / 1024, step: 'any', unit: 1024 ** 3 }, { key: 'minimumFreeBytes', label: 'freeGiB', min: 0, step: 'any', unit: 1024 ** 3 }, { key: 'downloadBytesPerSecond', label: 'downloadMiB', min: 0, step: 'any', unit: 1024 ** 2, kinds: ['Transcoding', 'Clipping'] }, { key: 'uploadBytesPerSecond', label: 'uploadMiB', min: 0, step: 'any', unit: 1024 ** 2 },
]
const configurationLabels: Record<WorkerKind, Partial<Record<NumericKey, string>>> = {
  Transcoding: {},
  Clipping: { maxConcurrentEncodes: 'clippingConcurrency', maxCacheBytes: 'workspaceGiB' },
  Recording: { maxConcurrentEncodes: 'recordingConcurrency', maxCacheBytes: 'recordingSpoolGiB' },
}
const titleKeys: Record<WorkerKind, string> = {
  Transcoding: 'transcoding.workers', Clipping: 'transcoding.clippingWorkers', Recording: 'transcoding.recordingWorkers',
}
const errorKeys: Record<WorkerKind, string> = {
  Transcoding: 'transcoding.invalidWorker', Clipping: 'transcoding.invalidClippingWorker', Recording: 'transcoding.invalidRecordingWorker',
}
const { t } = useI18n()
const props = withDefaults(defineProps<{ kind?: WorkerKind }>(), { kind: 'Transcoding' })
const isClipping = computed(() => props.kind === 'Clipping')
const isRecording = computed(() => props.kind === 'Recording')
const isTranscoding = computed(() => props.kind === 'Transcoding')
const titleKey = computed(() => titleKeys[props.kind])
const numericFields = computed(() => configurationFields.filter(field => !field.kinds || field.kinds.includes(props.kind))
  .map(field => ({ ...field, label: configurationLabels[props.kind][field.key] ?? field.label })))
const allWorkers = ref<Worker[]>([])
const workers = computed(() => allWorkers.value.filter(worker => (worker.kind ?? 'Transcoding') === props.kind))
const createFormId = useId()
const editFormId = useId()
const loading = ref(false)
const busy = ref(false)
const error = ref('')
const loadError = ref('')
const editError = ref('')
const createOpen = ref(false)
const newName = ref('')
const draft = ref<Worker>()
const mediaConfiguration = computed(() => draft.value?.kind === 'Recording' ? undefined : draft.value?.configuration)
const transcodingConfiguration = computed(() =>
{
  const value = draft.value
  return value && value.kind !== 'Clipping' && value.kind !== 'Recording' ? value.configuration : undefined
})
const secret = ref<{ workerId: string; value: string }>()
const deleteTarget = ref<Worker>()
const rotateTarget = ref<Worker>()
const logWorker = ref<Worker>()
let disposed = false
let requestRevision = 0
let controller: AbortController | undefined

function message(value: unknown): string { return value instanceof Error ? value.message : t('components.common.operationFailed') }
async function refresh(): Promise<void>
{
  const revision = ++requestRevision
  controller?.abort()
  controller = new AbortController()
  loading.value = true
  try { const result = await TranscodingAPI.workers(controller.signal); if (!disposed && revision === requestRevision) { allWorkers.value = result; loadError.value = '' } }
  catch (value) { if (!disposed && revision === requestRevision) { loadError.value = message(value) } }
  finally { if (!disposed && revision === requestRevision) { loading.value = false } }
}
async function act(action: () => Promise<void>, editing = false): Promise<void>
{
  if (busy.value || disposed) { return }
  busy.value = true
  error.value = editError.value = ''
  try { await action(); if (!disposed) { await refresh() } }
  catch (value) { if (!disposed) { if (editing) { editError.value = message(value) } else { error.value = message(value) } } }
  finally { if (!disposed) { busy.value = false } }
}
async function create(): Promise<void>
{
  if (!createOpen.value || !newName.value.trim() || newName.value.length > 100) { return }
  await act(async () => { const result = props.kind === 'Transcoding' ? await TranscodingAPI.createWorker(newName.value.trim()) : await TranscodingAPI.createWorker(newName.value.trim(), props.kind); if (!disposed) { createOpen.value = false; secret.value = { workerId: result.worker.id, value: result.secret }; newName.value = '' } })
}
function openCreate(): void { if (!busy.value) { newName.value = ''; error.value = ''; createOpen.value = true } }
function edit(worker: Worker): void { draft.value = normalizeWorker(worker); editError.value = '' }
function numberValue(key: NumericKey, unit: number): number | undefined
{
  const configuration = draft.value?.configuration
  if (!configuration || !(key in configuration)) { return undefined }
  const value: unknown = Reflect.get(configuration, key)
  return typeof value === 'number' ? value / unit : undefined
}
function setNumber(key: NumericKey, value: string | number | null, unit: number): void
{
  const configuration = draft.value?.configuration
  if (configuration && !busy.value && key in configuration)
  {
    Reflect.set(configuration, key, unit > 1 ? bandwidthInputBytes(value, unit) : value === null || value === '' ? Number.NaN : Number(value))
  }
}
async function save(): Promise<void>
{
  if (!draft.value) { return }
  const value = normalizeWorker(draft.value)
  if (!value.name.trim() || value.name.length > 100 || !isValidWorkerConfiguration(value.configuration, value.kind ?? 'Transcoding')) { editError.value = t(errorKeys[props.kind]); return }
  await act(async () => { await TranscodingAPI.updateWorker(value.id, { name: value.name.trim(), enabled: value.enabled, configuration: value.configuration, expectedRevision: value.configurationRevision }); if (!disposed) { draft.value = undefined } }, true)
}
async function remove(): Promise<void>
{
  const value = deleteTarget.value
  if (value) { await act(async () => { await TranscodingAPI.deleteWorker(value.id); if (!disposed) { deleteTarget.value = undefined } }) }
}
async function rotate(): Promise<void>
{
  const value = rotateTarget.value
  if (value) { await act(async () => { const result = await TranscodingAPI.rotateWorkerKey(value.id); if (!disposed) { secret.value = { workerId: value.id, value: result.secret }; rotateTarget.value = undefined } }) }
}
usePolling(refresh, 5000)
onBeforeUnmount(() => { disposed = true; requestRevision++; controller?.abort(); secret.value = undefined })
</script>

<style scoped>
.worker-list { display: flex; flex-direction: column; gap: 16px; }
.worker-row { border: 1px solid rgba(var(--v-theme-on-surface), .12); border-radius: 12px; padding: 16px; overflow-wrap: anywhere; }
.worker-fields { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: var(--ui-form-gap); }
@media (max-width: 600px) { .worker-fields { grid-template-columns: minmax(0, 1fr); } }
</style>
