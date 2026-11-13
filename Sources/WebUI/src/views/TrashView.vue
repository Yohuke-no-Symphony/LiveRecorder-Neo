<template>
  <PageHeader :title="t('views.trash.title')"><v-btn variant="outlined" prepend-icon="mdi-refresh" :loading="busy" @click="load(true)">{{ t('views.common.refresh') }}</v-btn></PageHeader>
  <v-alert v-if="error" type="error" class="mb-6">{{ error }}</v-alert>
  <v-card v-if="recordingVolume" class="pa-6 mb-7"><StorageBar :volume="recordingVolume" detailed /></v-card>
  <v-card v-if="entries.length"><v-table><thead><tr><th>{{ t('views.trash.name') }}</th><th>{{ t('views.trash.type') }}</th><th>{{ t('views.common.size') }}</th><th>{{ t('views.trash.purgeAt') }}</th><th /></tr></thead><tbody><tr v-for="entry in entries" :key="entry.id"><td class="py-5">{{ entry.title }}</td><td class="text-caption muted">{{ entry.kind === 'Clip' ? t('views.common.clips') : t('views.common.recording') }}</td><td class="text-caption mono">{{ formatBytes(entry.bytes) }}</td><td class="text-caption muted">{{ formatDate(entry.purgeAtUtc) }}</td><td><div class="action-buttons"><v-btn size="small" variant="text" color="primary" :disabled="busy" @click="restore(entry)">{{ t('views.trash.restore') }}</v-btn><v-btn icon="mdi-delete-forever-outline" size="small" variant="text" :aria-label="t('views.trash.purge')" @click="selected = entry" /></div></td></tr></tbody></v-table></v-card>
  <EmptyState v-else-if="!busy" :title="t('views.trash.empty')" icon="mdi-delete-outline" />
  <ConfirmDialog :model-value="!!selected" :title="t('views.trash.purgeConfirm')" :message="selected?.title" :action-label="t('views.trash.purge')" danger :busy="busy" :error="error" @update:model-value="!$event && (selected = undefined)" @confirm="purge" />
</template>

<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { computed, ref } from 'vue'
import { MediaAPI } from '@/apis/MediaAPI'
import { useAction } from '@/composables/useAction'
import { usePolling } from '@/composables/usePolling'
import type { StorageVolume, TrashEntry } from '@/models/media'
import { formatBytes, formatDate } from '@/utilities/format'
import PageHeader from '@/components/common/PageHeader.vue'
import EmptyState from '@/components/common/EmptyState.vue'
import ConfirmDialog from '@/components/common/ConfirmDialog.vue'
import StorageBar from '@/components/common/StorageBar.vue'

const { t } = useI18n()

const entries = ref<TrashEntry[]>([])
const volumes = ref<StorageVolume[]>([])
const recordingVolume = computed(() => volumes.value.find(volume => volume.id === 'recordings'))
const selected = ref<TrashEntry>()
const { busy, error, run } = useAction()
async function load(refresh = false): Promise<void>
{
  await run(async () => { [entries.value, volumes.value] = await Promise.all([MediaAPI.trash(), MediaAPI.storage(refresh)]) })
}
async function restore(entry: TrashEntry): Promise<void>
{
  await run(async () => { await MediaAPI.restoreTrash(entry.id); entries.value = await MediaAPI.trash(); volumes.value = await MediaAPI.storage(true) })
}
async function purge(): Promise<void>
{
  if (!selected.value) { return }
  await run(async () => { await MediaAPI.purgeTrash(selected.value!.id); selected.value = undefined; entries.value = await MediaAPI.trash(); volumes.value = await MediaAPI.storage(true) })
}
usePolling(() => load(), 15000)
</script>
