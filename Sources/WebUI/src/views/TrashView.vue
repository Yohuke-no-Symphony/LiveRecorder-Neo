<template>
  <PageHeader :title="t(management ? 'shell.nav.trashManagement' : 'shell.nav.trash')">
    <template v-if="selectedEntries.length">
      <span class="text-body-2" role="status">{{ t('views.trash.selected', { count: formatNumber(selectedEntries.length) }) }}</span>
      <v-btn variant="text" prepend-icon="mdi-close" :disabled="busy" @click="selectedIds = []">{{ t('views.trash.cancelSelection') }}</v-btn>
      <v-btn variant="outlined" color="error" prepend-icon="mdi-delete-forever-outline" :disabled="busy" @click="openPurge('selection', selectedEntries)">
        {{ t('views.trash.purgeSelected') }}
      </v-btn>
    </template>
    <v-btn v-else variant="outlined" color="error" prepend-icon="mdi-delete-sweep-outline" :disabled="busy || !purgeableEntries.length" @click="openPurge('clear', purgeableEntries)">
      {{ t('views.trash.clear') }}
    </v-btn>
    <v-btn variant="outlined" prepend-icon="mdi-refresh" :loading="busy" @click="load(true)">{{ t('views.common.refresh') }}</v-btn>
  </PageHeader>
  <v-alert v-if="error && !pendingPurge" type="error" class="trash-error mb-6">{{ error }}</v-alert>
  <v-card v-if="recordingVolume" class="card-content mb-7"><StorageBar :volume="recordingVolume" detailed /></v-card>
  <v-tabs v-if="management" v-model="activeKind" color="primary" show-arrows class="mb-5" :aria-label="t('views.trash.type')">
    <v-tab v-for="kind in tabs" :key="kind" :value="kind" :disabled="busy || !!pendingPurge">{{ kindLabel(kind) }}</v-tab>
  </v-tabs>
  <v-card>
    <v-data-table
      :model-value="selectedIds"
      v-model:page="page"
      :headers="headers"
      :items="visibleEntries"
      :item-selectable="canPurge"
      :show-select="purgeableEntries.length > 0"
      :select-all-label="t('views.trash.allSelectable')"
      :items-per-page="25"
      :items-per-page-options="[25, 50, 100]"
      :loading="busy && !visibleEntries.length"
      :no-data-text="t('views.trash.empty')"
      :aria-label="t(management ? 'shell.nav.trashManagement' : 'shell.nav.trash')"
      item-value="id"
      select-strategy="all"
      @update:model-value="updateSelection"
    >
      <template #item.title="{ item }">
        <div>{{ item.title }}</div>
        <template v-if="item.kind === 'Segment'">
          <div v-if="recordingTrashed(item)" class="text-caption muted mt-1">{{ t('views.trash.restoreRecordingFirst') }}</div>
          <router-link v-else-if="item.sessionId && auth.canViewRecordings" class="text-caption" :to="`/sessions/${item.sessionId}`">{{ t('views.trash.sourceRecording') }}</router-link>
          <div v-if="item.retainedInPlace" class="trash-segment-note text-caption muted mt-1">{{ t('views.trash.sharedSegment') }}</div>
        </template>
      </template>
      <template #item.bytes="{ item }">{{ formatBytes(item.bytes) }}</template>
      <template #item.purgeAtUtc="{ item }">{{ item.purgeAtUtc ? formatDate(item.purgeAtUtc) : t('shell.retention.noAutoPurge') }}</template>
      <template #item.actions="{ item }">
        <div class="action-buttons">
          <v-btn v-if="canRestore(item)" size="small" variant="text" color="primary" :disabled="busy || recordingTrashed(item)" @click="restore(item)">{{ t('views.trash.restore') }}</v-btn>
          <v-btn v-if="canPurge(item)" :disabled="busy" icon="mdi-delete-forever-outline" size="small" variant="text" :aria-label="t('views.trash.purge')" @click="openPurge('single', [item])" />
        </div>
      </template>
    </v-data-table>
  </v-card>
  <ConfirmDialog
    class="trash-confirm"
    :model-value="!!pendingPurge"
    :title="purgeTitle"
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
import ConfirmDialog from '@/components/common/ConfirmDialog.vue'
import StorageBar from '@/components/common/StorageBar.vue'

interface PendingPurge
{
  mode: 'single' | 'selection' | 'clear'
  entries: TrashEntry[]
}

const PURGE_BATCH_SIZE = 200
const PURGE_ORDER = new Map([['Segment', 0], ['Clip', 1], ['Session', 2]])
const props = defineProps<{ management?: boolean }>()
const { t } = useI18n()
const auth = useAuthStore()
const activeKind = ref('All')
const tabs = computed(() => ['All', 'Clip', 'Session', 'Segment'].filter(kind => kind === 'All' || canSeeManagedKind(kind)))
const entries = ref<TrashEntry[]>([])
const volumes = ref<StorageVolume[]>([])
const recordingVolume = computed(() => volumes.value.find(volume => volume.id === 'recordings'))
const page = ref(1)
const selectedIds = ref<string[]>([])
const pendingPurge = ref<PendingPurge>()
const { busy, error, run } = useAction()
const headers = computed(() => [
  { key: 'title', title: t('views.trash.name'), cellProps: { class: 'py-5' } },
  ...(props.management ? [{
    key: 'kind', title: t('views.trash.type'), value: (entry: TrashEntry) => kindLabel(entry.kind), cellProps: { class: 'text-caption muted' },
  }] : []),
  { key: 'bytes', title: t('views.common.size'), cellProps: { class: 'text-caption mono' } },
  { key: 'purgeAtUtc', title: t('views.trash.purgeAt'), cellProps: { class: 'text-caption muted' } },
  { key: 'actions', title: '', sortable: false },
])
const scopedEntries = computed(() => entries.value.filter(entry => props.management ? canSeeManagedKind(entry.kind)
  : entry.kind === 'Clip' && entry.ownerId === auth.user?.id))
const visibleEntries = computed(() => activeKind.value === 'All' ? scopedEntries.value
  : scopedEntries.value.filter(entry => entry.kind === activeKind.value))
const purgeableEntries = computed(() => visibleEntries.value.filter(canPurge))
const selectedEntries = computed(() =>
{
  const ids = new Set(selectedIds.value)
  return purgeableEntries.value.filter(entry => ids.has(entry.id))
})
const trashedRecordingIds = computed(() => new Set(scopedEntries.value.filter(entry => entry.kind === 'Session').map(entry => entry.entityId)))
const purgeTitle = computed(() =>
{
  if (pendingPurge.value?.mode !== 'clear') { return t('views.trash.purgeConfirm') }
  return activeKind.value === 'All' ? t('views.trash.clearConfirm')
    : t('views.trash.clearKindConfirm', { type: kindLabel(activeKind.value) })
})
const purgeConfirmation = computed(() =>
{
  const pending = pendingPurge.value
  if (!pending) { return undefined }
  if (pending.mode === 'single') { return pending.entries[0]?.title }
  const count = formatNumber(pending.entries.length)
  if (!props.management) { return t('views.trash.purgeCountConfirm', { count }) }
  const types = [...new Set(pending.entries.map(entry => entry.kind))].map(kindLabel).join(' / ')
  return t('views.trash.purgeKindsConfirm', { count, types })
})

function kindLabel(kind: string): string
{
  switch (kind)
  {
    case 'Clip': return t('views.common.clips')
    case 'Session': return t('views.common.recording')
    case 'Segment': return t('views.trash.segment')
    default: return t('views.common.all')
  }
}

function canSeeManagedKind(kind: string): boolean
{
  return kind === 'Clip' ? auth.canManageClips
    : (kind === 'Session' || kind === 'Segment') && (auth.canManageRecordings || auth.canPurgeRecordings)
}

function canManageClip(entry: TrashEntry): boolean
{
  return props.management ? auth.canManageClips : entry.ownerId === auth.user?.id
}

function canRestore(entry: TrashEntry): boolean
{
  return entry.kind === 'Clip' ? canManageClip(entry)
    : props.management === true && auth.canManageRecordings
}
function canPurge(entry: TrashEntry): boolean
{
  return entry.kind === 'Clip' ? canManageClip(entry)
    : props.management === true && auth.canPurgeRecordings
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
  const [list, storage] = await Promise.allSettled([MediaAPI.trash(props.management), MediaAPI.storage(refresh)])
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
    await MediaAPI.restoreTrash(entry.id, props.management)
    entries.value = entries.value.filter(item => item.id !== entry.id)
    await refreshEntriesAndStorage(true)
  })
}

function updateSelection(ids: string[]): void
{
  if (busy.value || pendingPurge.value) { return }
  selectedIds.value = ids
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
      const results = await MediaAPI.purgeTrashEntries(batch.map(entry => entry.id), props.management)
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
    await MediaAPI.purgeTrash(id, props.management)
    entries.value = entries.value.filter(entry => entry.id !== id)
    pendingPurge.value = undefined
    await refreshEntriesAndStorage(true)
  })
}

watch(purgeableEntries, available =>
{
  const availableIds = new Set(available.map(entry => entry.id))
  selectedIds.value = selectedIds.value.filter(id => availableIds.has(id))
})
watch(activeKind, () =>
{
  page.value = 1
  selectedIds.value = []
})
watch(tabs, available =>
{
  if (!available.includes(activeKind.value)) { activeKind.value = 'All' }
})
usePolling(() => load(), 15000)
</script>

<style scoped>
.trash-segment-note { max-width: 360px; }
.trash-error, .trash-confirm :deep(.v-alert) { white-space: pre-line; }
.trash-confirm :deep(.v-alert) { max-height: 40vh; overflow-y: auto; }
</style>
