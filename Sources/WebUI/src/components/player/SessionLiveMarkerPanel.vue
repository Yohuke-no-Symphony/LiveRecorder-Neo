<template>
  <section class="live-marker-panel" :aria-label="t('liveMarkers.title')">
    <CardHeader :title="t('liveMarkers.title')">
      <template #suffix><span v-if="detail" class="text-caption muted mono">{{ formatNumber(detail.entries.length) }}</span></template>
      <template #actions>
        <v-btn icon="mdi-crosshairs-gps" variant="text" size="small" :disabled="!hasSeekableEntries || loadingEntries" :title="t('liveMarkers.locate')" :aria-label="t('liveMarkers.locate')" @click="locateCurrentPosition" />
        <AiLiveMarkerActions v-if="auth.canViewRecordings" :can-generate="canGenerateAi && editable" :can-start="canStartAi && !saving" @generate="generateAi" @details="openAiDetails" />
        <v-btn icon="mdi-refresh" variant="text" size="small" :loading="loadingFiles" :disabled="saving" :aria-label="t('liveMarkers.refresh')" @click="refreshPanel" />
        <v-btn v-if="auth.canUploadLiveMarkers && editable" icon="mdi-upload" color="primary" variant="tonal" size="small" :disabled="saving || !defaultTimeZoneId" :aria-label="t('liveMarkers.upload')" @click="openUpload" />
      </template>
    </CardHeader>
    <v-progress-linear v-if="loadingFiles || loadingEntries" indeterminate color="primary" :aria-label="t('liveMarkers.loading')" />
    <v-alert v-if="error" type="error" density="compact" class="mx-4 my-3">{{ error }}</v-alert>
    <div v-if="files.length" class="marker-file-controls">
      <v-select :model-value="selectedId" :items="fileOptions" item-title="title" item-value="value" :label="t('liveMarkers.selectFile')" :disabled="saving" density="compact" hide-details @update:model-value="select(String($event ?? ''))" />
      <div v-if="selectedFile" class="marker-file-info">
        <span class="text-caption muted">{{ fileInfo(selectedFile) }}</span>
        <div v-if="selectedFile.canManage && editable" class="marker-file-actions">
          <v-btn icon="mdi-timer-edit-outline" variant="text" size="x-small" :disabled="saving" :aria-label="t('liveMarkers.align')" @click="openAlignment" />
          <v-btn icon="mdi-delete-outline" color="error" variant="text" size="x-small" :disabled="saving" :aria-label="t('liveMarkers.delete')" @click="openDelete" />
        </div>
      </div>
    </div>
    <v-virtual-scroll v-if="detail?.entries.length" ref="entriesViewport" class="marker-entry-scroll" :items="detail.entries" :item-height="100" item-key="index">
        <template #default="{ item }">
          <button type="button" class="marker-entry" :class="{ 'is-located': item.index === locatedEntryIndex }" :aria-current="item.index === locatedEntryIndex ? 'true' : undefined" :disabled="!canSeekLiveMarker(item.offsetMs, durationMs)" :aria-label="t('liveMarkers.jump', { time: formatLiveMarkerOffset(item.offsetMs), message: item.message })" @click="seek(item)">
            <span class="marker-color" :class="`marker-${item.markerColor.toLowerCase()}`" :title="item.markerColor" />
            <span class="marker-entry-content">
              <span class="marker-message">{{ item.message }}</span>
              <span class="marker-times mono">
                <v-tooltip :text="t('liveMarkers.recordingTime', { time: formatLiveMarkerOffset(item.offsetMs) })" location="top">
                  <template #activator="{ props: tooltipProps }">
                    <span v-bind="tooltipProps" class="marker-time" :aria-label="t('liveMarkers.recordingTime', { time: formatLiveMarkerOffset(item.offsetMs) })">
                      <v-icon icon="mdi-video-outline" size="14" aria-hidden="true" />
                      <span>{{ formatCompactLiveMarkerTime(formatLiveMarkerOffset(item.offsetMs)) }}</span>
                    </span>
                  </template>
                </v-tooltip>
                <v-tooltip :text="t('liveMarkers.liveTime', { time: item.liveTime })" location="top">
                  <template #activator="{ props: tooltipProps }">
                    <span v-bind="tooltipProps" class="marker-time" :aria-label="t('liveMarkers.liveTime', { time: item.liveTime })">
                      <v-icon icon="mdi-broadcast" size="14" aria-hidden="true" />
                      <span>{{ formatCompactLiveMarkerTime(item.liveTime) }}</span>
                    </span>
                  </template>
                </v-tooltip>
                <v-tooltip v-if="canSeekLiveMarker(item.offsetMs, durationMs)" :text="formatDate(item.realDateTime)" location="top">
                  <template #activator="{ props: tooltipProps }">
                    <span v-bind="tooltipProps" class="marker-time marker-real-time" :aria-label="formatDate(item.realDateTime)">
                      <v-icon icon="mdi-calendar-clock-outline" size="14" aria-hidden="true" />
                      <span>{{ formatDate(item.realDateTime) }}</span>
                    </span>
                  </template>
                </v-tooltip>
              </span>
              <span v-if="!canSeekLiveMarker(item.offsetMs, durationMs)" class="marker-unavailable">{{ t('liveMarkers.outOfRange') }}</span>
            </span>
            <v-icon icon="mdi-play-outline" size="18" />
          </button>
        </template>
    </v-virtual-scroll>
    <div v-else-if="!loadingFiles && !loadingEntries && !error" class="marker-empty">
      <v-icon icon="mdi-text-box-outline" size="30" />
      <span>{{ files.length ? t('liveMarkers.emptyEntries') : t('liveMarkers.emptyFiles') }}</span>
    </div>
    <SessionLiveMarkerUploadDialog v-model="uploadOpen" :default-time-zone-id="defaultTimeZoneId" :busy="saving" :error="actionError" @upload="uploadFile" />
    <AiAnalysisDetailsDialog v-model="aiDetailsOpen" purpose="LiveMarkers" :status="aiStatus" :loading="aiLoading" :busy="aiBusy" :error="aiError" :ended="ended" :can-cancel="canCancelAi" @refresh="refreshAi" @cancel="cancelAi" />
    <SessionLiveMarkerAlignmentDialog v-model="alignmentOpen" :file="editingFile" :busy="saving" :error="actionError" @save="saveAlignment" />
    <ConfirmDialog v-model="deleteOpen" :persistent="saving" :title="t('liveMarkers.delete')" :message="t('liveMarkers.deleteConfirm', { name: deletingFile?.originalFileName ?? '' })" :action-label="t('liveMarkers.delete')" :busy="saving" :error="actionError" danger @confirm="deleteFile" />
  </section>
</template>

<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useAuthStore } from '@/stores/auth'
import { useSessionLiveMarkers } from '@/composables/useSessionLiveMarkers'
import { useAiLiveMarkers } from '@/composables/useAiLiveMarkers'
import type { LiveMarkerEntry, LiveMarkerFile, LiveMarkerUpload } from '@/models/liveMarkers'
import { formatDate, formatNumber } from '@/utilities/format'
import { canSeekLiveMarker, formatCompactLiveMarkerTime, formatLiveMarkerOffset } from '@/utilities/liveMarkers'
import { findNearestPlaybackEntryIndex } from '@/utilities/nearestPlaybackEntry'
import CardHeader from '@/components/common/CardHeader.vue'
import ConfirmDialog from '@/components/common/ConfirmDialog.vue'
import SessionLiveMarkerUploadDialog from './SessionLiveMarkerUploadDialog.vue'
import SessionLiveMarkerAlignmentDialog from './SessionLiveMarkerAlignmentDialog.vue'
import AiLiveMarkerActions from './AiLiveMarkerActions.vue'
import AiAnalysisDetailsDialog from '@/components/analytics/AiAnalysisDetailsDialog.vue'

const props = withDefaults(defineProps<{ sessionId: string; durationMs: number; offsetMs?: number; active?: boolean; editable?: boolean; ended?: boolean; fileId?: string }>(), { offsetMs: 0, active: true, editable: true, ended: false })
const emit = defineEmits<{ seek: [offsetMs: number] }>()
const { t } = useI18n()
const auth = useAuthStore()
const {
  status: aiStatus, job: aiJob, loading: aiLoading, busy: aiBusy, error: aiError,
  canGenerate: canGenerateAi, canStart: canStartAi, canCancel: canCancelAi, initiatedJobId, scope: aiScope,
  generate: startAi, cancel: cancelAi, refresh: refreshAi,
} = useAiLiveMarkers(() => props.sessionId, () => props.ended && props.editable, () => props.active)
const { files, selectedId, detail, defaultTimeZoneId, loadingFiles, loadingEntries, saving, error, actionError, refresh, select, reveal, upload, align, remove, clearActionError } = useSessionLiveMarkers(() => props.sessionId, () => props.active && auth.canViewRecordings, () => props.fileId, aiScope)
const aiDetailsOpen = ref(false)
let localJobId = ''
let completedJobKey = ''
const selectedFile = computed(() => files.value.find(file => file.id === selectedId.value))
const fileOptions = computed(() => files.value.map(file => ({ value: file.id, title: `${file.originalFileName} · ${file.uploaderName || t('liveMarkers.unknownUploader')} · ${formatDate(file.uploadedAtUtc)}` })))
const uploadOpen = ref(false)
const alignmentOpen = ref(false)
const deleteOpen = ref(false)
const editingFile = ref<LiveMarkerFile>()
const deletingFile = ref<LiveMarkerFile>()
const entriesViewport = ref<{ scrollToIndex: (index: number, position: 'center') => void }>()
const locatedEntryIndex = ref<number>()
const hasSeekableEntries = computed(() => detail.value?.entries.some(entry => canSeekLiveMarker(entry.offsetMs, props.durationMs)) === true)
watch([selectedId, () => detail.value?.entries], () => { locatedEntryIndex.value = undefined }, { flush: 'sync' })

function locateCurrentPosition(): void
{
  const index = findNearestPlaybackEntryIndex(detail.value?.entries ?? [], props.offsetMs, props.durationMs)
  if (index < 0 || !detail.value || loadingEntries.value)
  {
    return
  }
  locatedEntryIndex.value = detail.value.entries[index]!.index
  entriesViewport.value?.scrollToIndex(index, 'center')
}

async function refreshPanel(): Promise<void>
{
  await Promise.all([refresh(), refreshAi()])
}

function openAiDetails(): void
{
  aiDetailsOpen.value = true
  void refreshAi()
}

function generateAi(): void
{
  if (canStartAi.value && !saving.value)
  {
    aiDetailsOpen.value = true
    void startAi()
  }
}

watch([() => props.sessionId, aiScope], () =>
{
  localJobId = ''
  completedJobKey = ''
  aiDetailsOpen.value = false
}, { flush: 'sync' })
watch(initiatedJobId, value => { if (value) { localJobId = value } }, { flush: 'sync' })
watch(aiJob, value =>
{
  if (value?.state !== 'Completed' || !value.liveMarkerFileId)
  {
    return
  }
  const key = `${value.id}:${value.liveMarkerFileId}`
  if (key === completedJobKey)
  {
    return
  }
  completedJobKey = key
  if (localJobId === value.id)
  {
    localJobId = ''
    void reveal(value.liveMarkerFileId)
  }
  else
  {
    void refresh(false)
  }
}, { flush: 'sync' })

function fileInfo(file: LiveMarkerFile): string
{
  return t('liveMarkers.fileInfo', { uploader: file.uploaderName || t('liveMarkers.unknownUploader'), uploadedAt: formatDate(file.uploadedAtUtc), count: formatNumber(file.entryCount) })
}

function seek(entry: LiveMarkerEntry): void
{
  if (canSeekLiveMarker(entry.offsetMs, props.durationMs)) { emit('seek', entry.offsetMs) }
}

function openUpload(): void
{
  clearActionError()
  uploadOpen.value = true
}

function openAlignment(): void
{
  clearActionError()
  editingFile.value = selectedFile.value
  alignmentOpen.value = true
}

function openDelete(): void
{
  clearActionError()
  deletingFile.value = selectedFile.value
  deleteOpen.value = true
}

async function uploadFile(value: LiveMarkerUpload): Promise<void>
{
  if (await upload(value)) { uploadOpen.value = false }
}

async function saveAlignment(calibrationMs: number): Promise<void>
{
  if (editingFile.value && await align(editingFile.value.id, calibrationMs)) { alignmentOpen.value = false }
}

async function deleteFile(): Promise<void>
{
  if (deletingFile.value && await remove(deletingFile.value.id)) { deleteOpen.value = false }
}
</script>

<style scoped>
.live-marker-panel { display: flex; flex-direction: column; min-height: 0; overflow: hidden; }
.marker-file-controls { padding: 4px var(--ui-card-padding) 12px; flex-shrink: 0; }
.marker-file-info { display: flex; align-items: center; gap: 6px; margin-top: 8px; min-width: 0; }
.marker-file-info > span { flex: 1; min-width: 0; overflow-wrap: anywhere; }
.marker-file-actions { display: flex; flex-shrink: 0; gap: 2px; }
.marker-entry-scroll { flex: 1; min-height: 0; overflow-y: auto; overscroll-behavior-y: contain; }
.marker-entry { display: flex; align-items: flex-start; gap: 10px; width: 100%; padding: 14px var(--ui-card-padding); border: 0; border-top: 1px solid rgba(var(--v-theme-on-surface), .06); background: transparent; color: inherit; text-align: left; cursor: pointer; }
.marker-entry:hover:not(:disabled) { background: rgba(var(--v-theme-on-surface), .035); }
.marker-entry.is-located { background: rgba(var(--v-theme-primary), .1); box-shadow: inset 3px 0 rgb(var(--v-theme-primary)); }
.marker-entry:focus-visible { outline: 2px solid rgb(var(--v-theme-primary)); outline-offset: -2px; }
.marker-entry:disabled { cursor: default; color: rgba(var(--v-theme-on-surface), .45); }
.marker-color { width: 8px; height: 8px; margin-top: 6px; border-radius: 50%; flex-shrink: 0; background: currentColor; }
.marker-red { color: rgb(var(--v-theme-error)); }.marker-orange { color: #e89143; }.marker-yellow { color: rgb(var(--v-theme-warning)); }.marker-green { color: rgb(var(--v-theme-success)); }.marker-blue { color: rgb(var(--v-theme-info)); }.marker-magenta { color: #c967b4; }.marker-grey { color: rgba(var(--v-theme-on-surface), .45); }
.marker-entry-content { flex: 1; min-width: 0; display: flex; flex-direction: column; gap: 6px; }
.marker-message { font-size: .86rem; line-height: 1.55; white-space: pre-wrap; overflow-wrap: anywhere; }
.marker-times { display: flex; align-items: center; gap: 6px; font-size: .65rem; opacity: .65; }
.marker-time { display: inline-flex; flex-shrink: 0; align-items: center; gap: 4px; white-space: nowrap; }
.marker-real-time { flex-shrink: 1; min-width: 0; overflow: hidden; }
.marker-real-time > span { min-width: 0; overflow: hidden; text-overflow: ellipsis; }
.marker-unavailable { font-size: .65rem; opacity: .65; }
.marker-empty { flex: 1; display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 12px; padding: 32px 16px; color: rgba(var(--v-theme-on-surface), .45); font-size: .85rem; }
</style>
