<template>
  <PageHeader :title="t('views.trash.title')"><v-btn variant="outlined" prepend-icon="mdi-refresh" :loading="busy" @click="load(true)">{{ t('views.common.refresh') }}</v-btn></PageHeader>
  <v-alert v-if="error" type="error" class="mb-6">{{ error }}</v-alert>
  <v-card v-if="recordingVolume" class="card-content mb-7"><StorageBar :volume="recordingVolume" detailed /></v-card>
  <v-card v-if="entries.length">
    <v-table>
      <thead><tr><th>{{ t('views.trash.name') }}</th><th>{{ t('views.trash.type') }}</th><th>{{ t('views.common.size') }}</th><th>{{ t('views.trash.purgeAt') }}</th><th /></tr></thead>
      <tbody>
        <tr v-for="entry in entries" :key="entry.id">
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
          <td>
            <div class="action-buttons">
              <v-btn v-if="canRestore(entry)" size="small" variant="text" color="primary" :disabled="busy || recordingTrashed(entry)" @click="restore(entry)">{{ t('views.trash.restore') }}</v-btn>
              <v-btn v-if="canPurge(entry)" :disabled="busy" icon="mdi-delete-forever-outline" size="small" variant="text" :aria-label="t('views.trash.purge')" @click="selected = entry" />
            </div>
          </td>
        </tr>
      </tbody>
    </v-table>
  </v-card>
  <EmptyState v-else-if="!busy" :title="t('views.trash.empty')" icon="mdi-delete-outline" />
  <ConfirmDialog :model-value="!!selected" :title="t('views.trash.purgeConfirm')" :message="selected?.title" :action-label="t('views.trash.purge')" danger :busy="busy" :error="error" @update:model-value="!$event && !busy && (selected = undefined)" @confirm="purge" />
</template>

<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { computed, ref } from 'vue'
import { MediaAPI } from '@/apis/MediaAPI'
import { useAction } from '@/composables/useAction'
import { usePolling } from '@/composables/usePolling'
import { useAuthStore } from '@/stores/auth'
import type { StorageVolume, TrashEntry } from '@/models/media'
import { formatBytes, formatDate } from '@/utilities/format'
import PageHeader from '@/components/common/PageHeader.vue'
import EmptyState from '@/components/common/EmptyState.vue'
import ConfirmDialog from '@/components/common/ConfirmDialog.vue'
import StorageBar from '@/components/common/StorageBar.vue'

const { t } = useI18n()

const auth = useAuthStore()
const entries = ref<TrashEntry[]>([])
const volumes = ref<StorageVolume[]>([])
const recordingVolume = computed(() => volumes.value.find(volume => volume.id === 'recordings'))
const selected = ref<TrashEntry>()
const { busy, error, run } = useAction()
function canRestore(entry: TrashEntry): boolean
{
  return entry.kind === 'Clip' || auth.canManageRecordings
}
function canPurge(entry: TrashEntry): boolean
{
  return entry.kind === 'Clip' || auth.canPurgeRecordings
}
function recordingTrashed(entry: TrashEntry): boolean
{
  return entry.kind === 'Segment' && entries.value.some(item => item.kind === 'Session' && item.entityId === entry.sessionId)
}
async function load(refresh = false): Promise<void>
{
  if (busy.value) { return }
  await run(async () => { [entries.value, volumes.value] = await Promise.all([MediaAPI.trash(), MediaAPI.storage(refresh)]) })
}
async function restore(entry: TrashEntry): Promise<void>
{
  if (busy.value || !canRestore(entry) || recordingTrashed(entry)) { return }
  await run(async () => { await MediaAPI.restoreTrash(entry.id); entries.value = await MediaAPI.trash(); volumes.value = await MediaAPI.storage(true) })
}
async function purge(): Promise<void>
{
  if (!selected.value || busy.value || !canPurge(selected.value)) { return }
  await run(async () => { await MediaAPI.purgeTrash(selected.value!.id); selected.value = undefined; entries.value = await MediaAPI.trash(); volumes.value = await MediaAPI.storage(true) })
}
usePolling(() => load(), 15000)
</script>

<style scoped>
.trash-segment-note { max-width: 360px; }
</style>
