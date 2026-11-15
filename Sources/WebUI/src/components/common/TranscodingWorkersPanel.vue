<template>
  <ContentCard :title="t('transcoding.workers')">
    <template #header-actions>
      <v-tooltip :text="t('transcoding.addWorker')"><template #activator="{ props: tooltipProps }"><v-btn v-bind="tooltipProps" icon="mdi-plus" variant="text" :disabled="busy" :aria-label="t('transcoding.addWorker')" @click="openCreate" /></template></v-tooltip>
      <v-btn icon="mdi-refresh" variant="text" :loading="loading" :aria-label="t('transcoding.refresh')" @click="refresh" />
    </template>
    <v-alert v-if="loadError || (error && !createOpen)" type="error" class="mb-4">{{ loadError || error }}</v-alert>
    <p v-if="!workers.length && !loading" class="muted">{{ t('transcoding.noWorkers') }}</p>
    <div class="worker-list">
      <article v-for="worker in workers" :key="worker.id" class="worker-row">
        <div class="d-flex align-center flex-wrap ga-3"><strong>{{ worker.name }}</strong><StatusChip :status="worker.enabled ? (isOnline(worker) ? 'active' : 'offline') : 'disabled'" /><span class="text-caption muted">{{ worker.capabilities?.operatingSystem }} {{ worker.capabilities?.architecture }}</span><v-spacer /><v-btn size="small" variant="text" @click="logWorker = worker">{{ t('transcoding.viewLogs') }}</v-btn><v-btn size="small" variant="text" :disabled="busy" @click="edit(worker)">{{ t('transcoding.editWorker') }}</v-btn><v-btn size="small" variant="text" :disabled="busy" @click="rotateTarget = worker">{{ t('transcoding.rotateKey') }}</v-btn><v-btn icon="mdi-delete-outline" size="small" variant="text" color="error" :disabled="busy" :aria-label="t('transcoding.deleteWorker')" @click="deleteTarget = worker" /></div>
        <div class="text-caption muted mt-2">{{ t('transcoding.workerId') }}: {{ worker.id }} · {{ t('transcoding.lastSeen') }}: {{ worker.lastSeenAtUtc ? formatDate(worker.lastSeenAtUtc) : '—' }}</div>
        <div v-if="worker.telemetry" class="text-caption mt-2">CPU {{ worker.telemetry.cpuPercent === null ? '—' : `${Math.round(worker.telemetry.cpuPercent)}%` }} · GPU {{ worker.telemetry.runningGpu }} · CPU {{ worker.telemetry.runningCpu }} · {{ formatBytes(worker.telemetry.freeBytes) }}</div>
        <div v-if="worker.capabilities?.encoders.length" class="d-flex flex-wrap ga-1 mt-2"><v-chip v-for="encoder in worker.capabilities.encoders" :key="`${encoder.encoder}:${encoder.deviceId}`" size="x-small" :color="encoder.available ? 'success' : 'warning'" :title="encoder.error || undefined">{{ encoder.encoder }}<template v-if="encoder.deviceId"> · {{ encoder.deviceId }}</template></v-chip></div>
        <v-alert v-if="worker.configurationError" type="error" density="compact" class="mt-3">{{ worker.configurationError }}</v-alert>
        <p v-else-if="worker.configurationRevision !== worker.appliedConfigurationRevision" class="text-caption text-warning mt-2">{{ t('transcoding.configurationPending') }}</p>
        <p v-if="worker.telemetry?.cpuUnavailableReason" class="text-caption muted mt-2">{{ worker.telemetry.cpuUnavailableReason }}</p>
      </article>
    </div>
    <v-dialog :model-value="createOpen" max-width="480" :persistent="busy" @update:model-value="!$event && !busy && (createOpen = false)">
      <ContentCard :title="t('transcoding.addWorker')">
        <v-alert v-if="error" type="error" class="mb-4">{{ error }}</v-alert>
        <v-text-field v-model="newName" maxlength="100" :label="t('transcoding.workerName')" :disabled="busy" autofocus @keydown.enter.prevent="create" />
        <template #actions><v-btn variant="text" :disabled="busy" @click="createOpen = false">{{ t('transcoding.cancel') }}</v-btn><v-btn color="primary" :disabled="!newName.trim() || newName.length > 100" :loading="busy" @click="create">{{ t('transcoding.addWorker') }}</v-btn></template>
      </ContentCard>
    </v-dialog>
    <v-dialog :model-value="!!draft" max-width="900" :persistent="busy" @update:model-value="!$event && !busy && (draft = undefined)">
      <ContentCard v-if="draft" :title="t('transcoding.workerSettings')">
        <v-alert v-if="editError" type="error" class="mb-4">{{ editError }}</v-alert>
        <div class="worker-fields"><v-text-field v-model="draft.name" maxlength="100" :label="t('transcoding.workerName')" :disabled="busy" /><v-switch v-model="draft.enabled" :label="t('transcoding.workerEnabled')" :disabled="busy" /></div>
        <div class="worker-fields"><v-text-field v-model="draft.configuration.ffmpegPath" :label="t('shell.settings.ffmpeg')" :disabled="busy" /><v-text-field v-model="draft.configuration.ffprobePath" :label="t('shell.settings.ffprobe')" :disabled="busy" /><v-text-field v-model="draft.configuration.tempDirectory" :label="t('transcoding.tempDirectory')" :disabled="busy" /><v-select :items="['auto', 'h264_qsv', 'h264_nvenc', 'h264_amf', 'h264_vaapi', 'h264_videotoolbox']" v-model="draft.configuration.preferredEncoder" :label="t('transcoding.preferredEncoder')" :disabled="busy" /><v-text-field v-model="draft.configuration.deviceId" :label="t('transcoding.deviceId')" clearable :disabled="busy" /></div>
        <h3 class="text-subtitle-1 mb-4">{{ t('transcoding.scheduling') }}</h3>
        <v-switch :model-value="draft.configuration.cpuFallbackMode === 'Balanced'" :label="t('transcoding.cpuFallback')" :hint="t('transcoding.cpuFallbackHint')" persistent-hint :disabled="busy" @update:model-value="draft.configuration.cpuFallbackMode = $event ? 'Balanced' : 'Disabled'" />
        <div class="worker-fields mt-4"><v-text-field v-for="field in numericFields" :key="field.key" :model-value="draft.configuration[field.key] / (field.unit ?? 1)" :label="t(`transcoding.${field.label}`)" type="number" :min="field.min" :max="field.max" :step="field.step ?? 1" :disabled="busy" @update:model-value="setNumber(field.key, $event, field.unit ?? 1)" /></div>
        <template #actions><v-btn variant="text" :disabled="busy" @click="draft = undefined">{{ t('transcoding.cancel') }}</v-btn><v-btn color="primary" :loading="busy" @click="save">{{ t('transcoding.saveWorker') }}</v-btn></template>
      </ContentCard>
    </v-dialog>
    <v-dialog :model-value="!!secret" max-width="650" @update:model-value="!$event && (secret = undefined)"><ContentCard v-if="secret" :title="t('transcoding.token')"><v-alert type="info" class="mb-4">{{ t('transcoding.keyCreated') }}</v-alert><v-text-field :model-value="secret.workerId" :label="t('transcoding.workerId')" readonly /><v-textarea :model-value="secret.value" :label="t('transcoding.token')" readonly rows="3" /><template #actions><v-btn color="primary" @click="secret = undefined">{{ t('transcoding.close') }}</v-btn></template></ContentCard></v-dialog>
    <ConfirmDialog :model-value="!!deleteTarget" :title="t('transcoding.deleteWorker')" :message="t('transcoding.deleteWorkerConfirm')" :action-label="t('transcoding.delete')" danger :busy="busy" :error="error" @update:model-value="!$event && !busy && (deleteTarget = undefined)" @confirm="remove" />
    <ConfirmDialog :model-value="!!rotateTarget" :title="t('transcoding.rotateKey')" :message="t('transcoding.rotateKeyConfirm')" :action-label="t('transcoding.rotateKey')" :busy="busy" :error="error" @update:model-value="!$event && !busy && (rotateTarget = undefined)" @confirm="rotate" />
    <TranscodingLogDialog v-if="logWorker" :worker="logWorker" @close="logWorker = undefined" />
  </ContentCard>
</template>

<script setup lang="ts">
import { onBeforeUnmount, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { bandwidthInputBytes } from '@/models/bandwidth'
import { TranscodingAPI } from '@/apis/TranscodingAPI'
import { usePolling } from '@/composables/usePolling'
import { isValidWorkerConfiguration, type TranscodingWorker, type WorkerConfiguration } from '@/models/transcoding'
import { formatBytes, formatDate } from '@/utilities/format'
import ContentCard from './ContentCard.vue'
import ConfirmDialog from './ConfirmDialog.vue'
import StatusChip from './StatusChip.vue'
import TranscodingLogDialog from './TranscodingLogDialog.vue'

type NumericKey = { [K in keyof WorkerConfiguration]: WorkerConfiguration[K] extends number ? K : never }[keyof WorkerConfiguration]
const numericFields: { key: NumericKey; label: string; min: number; max?: number; step?: number; unit?: number }[] = [
  { key: 'maxConcurrentEncodes', label: 'totalConcurrency', min: 1, max: 64 }, { key: 'gpuConcurrency', label: 'gpuConcurrency', min: 0, max: 64 }, { key: 'cpuConcurrency', label: 'cpuConcurrency', min: 0, max: 64 },
  { key: 'gpuWaitSeconds', label: 'cpuIdleSeconds', min: 0, max: 86400 }, { key: 'cpuSampleWindowSeconds', label: 'cpuAverageSeconds', min: 5, max: 300 }, { key: 'cpuLoadThreshold', label: 'cpuThreshold', min: 1, max: 100 }, { key: 'cpuThreadFraction', label: 'cpuThreadFraction', min: .01, max: 1, step: .05 },
  { key: 'maxCacheBytes', label: 'cacheGiB', min: 1 / 1024, step: .5, unit: 1024 ** 3 }, { key: 'minimumFreeBytes', label: 'freeGiB', min: 0, step: .5, unit: 1024 ** 3 }, { key: 'downloadBytesPerSecond', label: 'downloadMiB', min: 0, step: .5, unit: 1024 ** 2 }, { key: 'uploadBytesPerSecond', label: 'uploadMiB', min: 0, step: .5, unit: 1024 ** 2 },
]
const { t } = useI18n()
const workers = ref<TranscodingWorker[]>([])
const loading = ref(false)
const busy = ref(false)
const error = ref('')
const loadError = ref('')
const editError = ref('')
const createOpen = ref(false)
const newName = ref('')
const draft = ref<TranscodingWorker>()
const secret = ref<{ workerId: string; value: string }>()
const deleteTarget = ref<TranscodingWorker>()
const rotateTarget = ref<TranscodingWorker>()
const logWorker = ref<TranscodingWorker>()
let disposed = false
let requestRevision = 0
let controller: AbortController | undefined

function isOnline(worker: TranscodingWorker): boolean { return !!worker.lastSeenAtUtc && Date.now() - Date.parse(worker.lastSeenAtUtc) < 45000 }
function message(value: unknown): string { return value instanceof Error ? value.message : t('components.common.operationFailed') }
async function refresh(): Promise<void>
{
  const revision = ++requestRevision
  controller?.abort()
  controller = new AbortController()
  loading.value = true
  try { const result = await TranscodingAPI.workers(controller.signal); if (!disposed && revision === requestRevision) { workers.value = result; loadError.value = '' } }
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
  await act(async () => { const result = await TranscodingAPI.createWorker(newName.value.trim()); if (!disposed) { createOpen.value = false; secret.value = { workerId: result.worker.id, value: result.secret }; newName.value = '' } })
}
function openCreate(): void { if (!busy.value) { newName.value = ''; error.value = ''; createOpen.value = true } }
function edit(worker: TranscodingWorker): void { draft.value = JSON.parse(JSON.stringify(worker)) as TranscodingWorker; editError.value = '' }
function setNumber(key: NumericKey, value: string | number | null, unit: number): void
{
  if (draft.value && !busy.value) { draft.value.configuration[key] = unit > 1 ? bandwidthInputBytes(value, unit) : value === null || value === '' ? Number.NaN : Number(value) }
}
async function save(): Promise<void>
{
  const value = draft.value
  if (!value) { return }
  if (!value.name.trim() || value.name.length > 100 || !isValidWorkerConfiguration(value.configuration)) { editError.value = t('transcoding.invalidWorker'); return }
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
.worker-fields { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 16px; }
@media (max-width: 600px) { .worker-fields { grid-template-columns: minmax(0, 1fr); } }
</style>
