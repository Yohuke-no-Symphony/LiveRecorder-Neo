<template>
  <PageHeader :title="t('views.trash.title')">
    <template v-if="selectionMode">
      <span class="text-body-2" role="status">{{ t('views.trash.selected', { count: formatNumber(selectedEntries.length) }) }}</span>
      <v-btn variant="text" prepend-icon="mdi-close" :disabled="busy" @click="cancelSelection">{{ t('views.trash.cancelSelection') }}</v-btn>
      <v-btn variant="outlined" color="error" prepend-icon="mdi-delete-forever-outline" :disabled="busy || !selectedEntries.length" @click="openPurge('selection', selectedEntries)">
        {{ t('views.trash.purgeSelected') }}
      </v-btn>
    </template>
    <template v-else>
      <v-btn variant="outlined" prepend-icon="mdi-checkbox-multiple-outline" :disabled="busy || !purgeableEntries.length" @click="selectionMode = true">
        {{ t('views.trash.select') }}
      </v-btn>
      <v-btn variant="outlined" color="error" prepend-icon="mdi-delete-sweep-outline" :disabled="busy || !purgeableEntries.length" @click="openPurge('clear', purgeableEntries)">
        {{ t('views.trash.clear') }}
      </v-btn>
    </template>
    <v-btn variant="outlined" prepend-icon="mdi-refresh" :loading="busy" @click="load(true)">{{ t('views.common.refresh') }}</v-btn>
  </PageHeader>
  <v-alert v-if="error && !pendingPurge" type="error" class="trash-error mb-6">{{ error }}</v-alert>
  <v-card v-if="recordingVolume" class="card-content mb-7"><StorageBar :volume="recordingVolume" detailed /></v-card>
  <v-card v-if="entries.length">
    <v-table>
      <thead>
        <tr>
          <th v-if="selectionMode" class="trash-select-column" scope="col">
            <v-checkbox-btn
              :model-value="allSelected"
              :indeterminate="!allSelected && !!selectedEntries.length"
              :aria-label="t('views.trash.allSelectable')"
              :disabled="busy || !purgeableEntries.length"
              @update:model-value="selectAll($event === true)"
            />
          </th>
          <th scope="col">{{ t('views.trash.name') }}</th>
          <th scope="col">{{ t('views.trash.type') }}</th>
          <th scope="col">{{ t('views.common.size') }}</th>
          <th scope="col">{{ t('views.trash.purgeAt') }}</th>
          <th v-if="!selectionMode" />
        </tr>
      </thead>
      <tbody>
        <tr v-for="entry in entries" :key="entry.id">
          <td v-if="selectionMode" class="trash-select-column">
            <v-checkbox-btn
              v-if="canPurge(entry)"
              :model-value="selectedIds.has(entry.id)"
              :aria-label="t('views.trash.selectEntry', { title: entry.title })"
              :disabled="busy"
              @update:model-value="selectEntry(entry, $event === true)"
            />
          </td>
          <td class="py-5">
            <div>{{ entry.title }}</div>
            <template v-if="entry.kind === 'Segment'">
              <div v-if="recordingTrashed(entry)" class="text-caption muted mt-1">{{ t('views.trash.restoreRecordingFirst') }}</div>
              <router-link v-else-if="entry.sessionId && auth.canViewRecordings" class="text-caption" :to="`/sessions/${entry.sessionId}`">{{ t('views.trash.sourceRecording') }}</router-link>
              <div v-if="entry.retainedInPlace" class="trash-segment-note text-caption muted mt-1">{{ t('views.trash.sharedSegment') }}</div>
            </template>
          </td>
          <td class="text-caption muted">{{ entry.kind === 'Clip' ? t('views.common.clips') : entry.kind === 'Segment' ? t('views.trash.segment') : t('views.common.recording') }}</td>
          <td class="text-caption mono">{{ formatBytes(entry.bytes) }}</td>
          <td class="text-caption muted">{{ entry.purgeAtUtc ? formatDate(entry.purgeAtUtc) : t('shell.retention.noAutoPurge') }}</td>
          <td v-if="!selectionMode">
            <div class="action-buttons">
              <v-btn v-if="canRestore(entry)" size="small" variant="text" color="primary" :disabled="busy || recordingTrashed(entry)" @click="restore(entry)">{{ t('views.trash.restore') }}</v-btn>
              <v-btn v-if="canPurge(entry)" :disabled="busy" icon="mdi-delete-forever-outline" size="small" variant="text" :aria-label="t('views.trash.purge')" @click="openPurge('single', [entry])" />
            </div>
          </td>
        </tr>
      </tbody>
    </v-table>
  </v-card>
  <EmptyState v-else-if="!busy" :title="t('views.trash.empty')" icon="mdi-delete-outline" />
  <ConfirmDialog
    class="trash-confirm"
    :model-value="!!pendingPurge"
    :title="t(pendingPurge?.mode === 'clear' ? 'views.trash.clearConfirm' : 'views.trash.purgeConfirm')"
    :message="purgeConfirmation"
    :action-label="t('views.trash.purge')"
    danger
    :busy="busy"
    :error="error"
    @update:model-value="!$event && !busy && (pendingPurge = undefined)"
    @confirm="purge"
  />
</template>

<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { computed, ref, watch } from 'vue'
import { MediaAPI } from '@/apis/MediaAPI'
import { useAction } from '@/composables/useAction'
import { usePolling } from '@/composables/usePolling'
import { useAuthStore } from '@/stores/auth'
import type { StorageVolume, TrashEntry } from '@/models/media'
import { formatBytes, formatDate, formatNumber } from '@/utilities/format'
import PageHeader from '@/components/common/PageHeader.vue'
import EmptyState from '@/components/common/EmptyState.vue'
import ConfirmDialog from '@/components/common/ConfirmDialog.vue'
import StorageBar from '@/components/common/StorageBar.vue'

interface PendingPurge
{
  mode: 'single' | 'selection' | 'clear'
  entries: TrashEntry[]
}

const PURGE_BATCH_SIZE = 200
const PURGE_ORDER = new Map([['Segment', 0], ['Clip', 1], ['Session', 2]])
const { t } = useI18n()
const auth = useAuthStore()
const entries = ref<TrashEntry[]>([])
const volumes = ref<StorageVolume[]>([])
const recordingVolume = computed(() => volumes.value.find(volume => volume.id === 'recordings'))
const selectionMode = ref(false)
const selectedIds = ref(new Set<string>())
const pendingPurge = ref<PendingPurge>()
const { busy, error, run } = useAction()
const purgeableEntries = computed(() => entries.value.filter(canPurge))
const selectedEntries = computed(() => purgeableEntries.value.filter(entry => selectedIds.value.has(entry.id)))
const allSelected = computed(() => !!purgeableEntries.value.length && selectedEntries.value.length === purgeableEntries.value.length)
const trashedRecordingIds = computed(() => new Set(entries.value.filter(entry => entry.kind === 'Session').map(entry => entry.entityId)))
const purgeConfirmation = computed(() =>
{
  const pending = pendingPurge.value
  if (!pending) { return undefined }
  return pending.mode === 'single' ? pending.entries[0]?.title
    : t('views.trash.purgeCountConfirm', { count: formatNumber(pending.entries.length) })
})

function canRestore(entry: TrashEntry): boolean
{
  return entry.kind === 'Clip' || auth.canManageRecordings
}
function canPurge(entry: TrashEntry): boolean
{
  return entry.kind === 'Clip' ? auth.isAdmin || entry.ownerId === auth.user?.id : auth.canPurgeRecordings
}

function recordingTrashed(entry: TrashEntry): boolean
{
  return entry.kind === 'Segment' && !!entry.sessionId && trashedRecordingIds.value.has(entry.sessionId)
}

function errorMessage(value: unknown): string
{
  return value instanceof Error && value.message ? value.message : t('components.common.operationFailed')
}

async function refreshEntriesAndStorage(refresh: boolean): Promise<void>
{
  const [list, storage] = await Promise.allSettled([MediaAPI.trash(), MediaAPI.storage(refresh)])
  if (list.status === 'fulfilled') { entries.value = list.value }
  if (storage.status === 'fulfilled') { volumes.value = storage.value }
  error.value = [list, storage].flatMap(result => result.status === 'rejected' ? [errorMessage(result.reason)] : []).join('\n')
}

async function load(refresh = false): Promise<void>
{
  if (busy.value || pendingPurge.value) { return }
  await run(() => refreshEntriesAndStorage(refresh))
}

async function restore(entry: TrashEntry): Promise<void>
{
  if (busy.value || pendingPurge.value || !canRestore(entry) || recordingTrashed(entry)) { return }
  await run(async () =>
  {
    await MediaAPI.restoreTrash(entry.id)
    entries.value = entries.value.filter(item => item.id !== entry.id)
    await refreshEntriesAndStorage(true)
  })
}

function selectAll(selected: boolean): void
{
  if (busy.value || !selectionMode.value || pendingPurge.value) { return }
  selectedIds.value = new Set(selected ? purgeableEntries.value.map(entry => entry.id) : [])
}

function selectEntry(entry: TrashEntry, selected: boolean): void
{
  if (busy.value || !selectionMode.value || pendingPurge.value || !canPurge(entry)) { return }
  if (selected) { selectedIds.value.add(entry.id) }
  else { selectedIds.value.delete(entry.id) }
}

function cancelSelection(): void
{
  if (busy.value || pendingPurge.value) { return }
  selectionMode.value = false
  selectedIds.value.clear()
}

function openPurge(mode: PendingPurge['mode'], targets: TrashEntry[]): void
{
  if (busy.value || pendingPurge.value) { return }
  const targetEntries = targets.filter(canPurge)
  if (!targetEntries.length) { return }
  error.value = ''
  pendingPurge.value = { mode, entries: targetEntries }
}

async function purgeBatch(pending: PendingPurge): Promise<void>
{
  const ordered = [...pending.entries].sort((a, b) => (PURGE_ORDER.get(a.kind) ?? 1) - (PURGE_ORDER.get(b.kind) ?? 1))
  const completed = new Set<string>()
  const failures: string[] = []
  for (let offset = 0; offset < ordered.length; offset += PURGE_BATCH_SIZE)
  {
    const batch = ordered.slice(offset, offset + PURGE_BATCH_SIZE)
    try
    {
      const results = await MediaAPI.purgeTrashEntries(batch.map(entry => entry.id))
      const byId = new Map(results.map(result => [result.entryId, result]))
      for (const entry of batch)
      {
        const result = byId.get(entry.id)
        if (result?.success)
        {
          completed.add(entry.id)
        }
        else
        {
          failures.push(t('views.trash.purgeFailure', { title: entry.title, message: result?.error || t('components.common.operationFailed') }))
        }
      }
    }
    catch (value)
    {
      failures.push(errorMessage(value))
      break
    }
  }

  entries.value = entries.value.filter(entry => !completed.has(entry.id))
  await refreshEntriesAndStorage(true)
  const purgeableIds = new Set(purgeableEntries.value.map(entry => entry.id))
  const remaining = pending.entries.filter(entry => !completed.has(entry.id) && purgeableIds.has(entry.id))
  pendingPurge.value = remaining.length ? { ...pending, entries: remaining } : undefined
  if (!remaining.length && !selectedEntries.value.length) { selectionMode.value = false }
  if (error.value) { failures.push(error.value) }
  error.value = failures.join('\n')
}

async function purge(): Promise<void>
{
  const pending = pendingPurge.value
  if (!pending || busy.value) { return }
  const targets = pending.entries.filter(canPurge)
  if (targets.length !== pending.entries.length)
  {
    pendingPurge.value = targets.length ? { ...pending, entries: targets } : undefined
    error.value = t('views.trash.selectionChanged')
    return
  }

  await run(async () =>
  {
    if (pending.mode !== 'single')
    {
      await purgeBatch(pending)
      return
    }
    const id = pending.entries[0]!.id
    await MediaAPI.purgeTrash(id)
    entries.value = entries.value.filter(entry => entry.id !== id)
    pendingPurge.value = undefined
    await refreshEntriesAndStorage(true)
  })
}

watch(purgeableEntries, available =>
{
  const availableIds = new Set(available.map(entry => entry.id))
  selectedIds.value = new Set([...selectedIds.value].filter(id => availableIds.has(id)))
  if (!available.length) { selectionMode.value = false }
})
usePolling(() => load(), 15000)
</script>

<style scoped>
.trash-segment-note { max-width: 360px; }
.trash-select-column { width: 1%; }
.trash-error, .trash-confirm :deep(.v-alert) { white-space: pre-line; }
.trash-confirm :deep(.v-alert) { max-height: 40vh; overflow-y: auto; }
</style>
