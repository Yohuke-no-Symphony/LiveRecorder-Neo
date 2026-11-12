<template>
  <PageHeader title="回收站"><v-btn variant="outlined" prepend-icon="mdi-refresh" :loading="busy" @click="load">刷新</v-btn></PageHeader>
  <v-alert v-if="error" type="error" class="mb-6">{{ error }}</v-alert>
  <div class="settings-grid mb-7"><v-card v-for="volume in volumes" :key="volume.id" class="pa-6"><div class="text-caption muted mb-5 text-truncate">{{ volume.path }}</div><StorageBar :volume="volume" detailed /></v-card></div>
  <v-card v-if="entries.length"><v-table><thead><tr><th>名称</th><th>类型</th><th>大小</th><th>彻底删除时间</th><th /></tr></thead><tbody><tr v-for="entry in entries" :key="entry.id"><td class="py-5">{{ entry.title }}</td><td class="text-caption muted">{{ entry.kind === 'Clip' ? '切片' : '录播' }}</td><td class="text-caption mono">{{ formatBytes(entry.bytes) }}</td><td class="text-caption muted">{{ formatDate(entry.purgeAtUtc) }}</td><td><div class="action-buttons"><v-btn size="small" variant="text" color="primary" :disabled="busy" @click="restore(entry)">恢复</v-btn><v-btn icon="mdi-delete-forever-outline" size="small" variant="text" aria-label="彻底删除" @click="selected = entry" /></div></td></tr></tbody></v-table></v-card>
  <EmptyState v-else-if="!busy" title="回收站为空" icon="mdi-delete-outline" />
  <ConfirmDialog :model-value="!!selected" title="彻底删除？" :message="selected?.title" action-label="彻底删除" danger :busy="busy" :error="error" @update:model-value="!$event && (selected = undefined)" @confirm="purge" />
</template>

<script setup lang="ts">
import { ref } from 'vue'
import { MediaAPI } from '@/apis/MediaAPI'
import { useAction } from '@/composables/useAction'
import { usePolling } from '@/composables/usePolling'
import type { StorageVolume, TrashEntry } from '@/models/media'
import { formatBytes, formatDate } from '@/utilities/format'
import PageHeader from '@/components/common/PageHeader.vue'
import EmptyState from '@/components/common/EmptyState.vue'
import ConfirmDialog from '@/components/common/ConfirmDialog.vue'
import StorageBar from '@/components/common/StorageBar.vue'

const entries = ref<TrashEntry[]>([])
const volumes = ref<StorageVolume[]>([])
const selected = ref<TrashEntry>()
const { busy, error, run } = useAction()
async function load(): Promise<void>
{
  await run(async () => { [entries.value, volumes.value] = await Promise.all([MediaAPI.trash(), MediaAPI.storage()]) })
}
async function restore(entry: TrashEntry): Promise<void>
{
  await run(async () => { await MediaAPI.restoreTrash(entry.id); entries.value = await MediaAPI.trash(); volumes.value = await MediaAPI.storage() })
}
async function purge(): Promise<void>
{
  if (!selected.value) { return }
  await run(async () => { await MediaAPI.purgeTrash(selected.value!.id); selected.value = undefined; entries.value = await MediaAPI.trash(); volumes.value = await MediaAPI.storage() })
}
usePolling(load, 15000)
</script>
