<template>
  <PageHeader :title="t('views.storage.title')">
    <v-btn variant="outlined" prepend-icon="mdi-refresh" :loading="busy" @click="load(true)">{{ t('views.common.refresh') }}</v-btn>
  </PageHeader>
  <v-alert v-if="error" type="error" class="mb-6">{{ error }}</v-alert>
  <template v-if="report">
    <p class="text-caption muted mb-5">{{ t('views.storage.scannedAt', { date: formatDate(report.scannedAtUtc) }) }}</p>
    <v-alert v-if="report.isPartial" type="warning" class="mb-6">{{ t('views.storage.partial') }}</v-alert>
    <div class="settings-grid mb-4">
      <v-card v-for="volume in report.volumes" :key="volume.id" class="pa-6">
        <StorageBar :volume="volume" :title="t(volume.id === 'recordings' ? 'components.storage.recordingSpace' : 'components.storage.dataSpace')" detailed />
        <div class="storage-root text-caption muted mt-4">{{ volume.path }}</div>
      </v-card>
    </div>
    <v-tabs v-model="tab" color="primary" class="mb-5">
      <v-tab value="rooms">{{ t('views.storage.rooms') }}</v-tab>
      <v-tab value="users">{{ t('views.storage.users') }}</v-tab>
      <v-tab value="directories">{{ t('views.storage.directories') }}</v-tab>
    </v-tabs>
    <div class="filter-bar"><v-text-field v-model="search" :label="t('views.storage.search')" prepend-inner-icon="mdi-magnify" clearable density="compact" style="max-width: 400px" /></div>
    <StorageUsageTable v-if="visibleRows.length" :rows="visibleRows" />
    <EmptyState v-else :title="t('views.storage.empty')" icon="mdi-harddisk" />
  </template>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { MediaAPI } from '@/apis/MediaAPI'
import { useAction } from '@/composables/useAction'
import { usePolling } from '@/composables/usePolling'
import type { StorageUsageReport, StorageUsageRow } from '@/models/storage'
import { formatDate } from '@/utilities/format'
import PageHeader from '@/components/common/PageHeader.vue'
import EmptyState from '@/components/common/EmptyState.vue'
import StorageBar from '@/components/common/StorageBar.vue'
import StorageUsageTable from '@/components/storage/StorageUsageTable.vue'

const { t } = useI18n()
const report = ref<StorageUsageReport>()
const tab = ref<'rooms' | 'users' | 'directories'>('rooms')
const search = ref<string | null>('')
const { busy, error, run } = useAction()
const visibleRows = computed<StorageUsageRow[]>(() =>
{
  const current = report.value
  if (!current) { return [] }
  const rows = tab.value === 'directories'
    ? current.directories.map(row => ({ ...row, name: t(`views.storage.kinds.${row.kind}`) }))
    : tab.value === 'rooms'
      ? current.rooms.map(row => ({ ...row, name: row.roomId ? `${row.name || row.id} · ${t('views.streamer.room', { roomId: row.roomId })}` : row.name }))
      : current.users
  const term = (search.value ?? '').trim().toLocaleLowerCase()
  return rows.filter(row => `${row.name} ${row.id} ${row.path}`.toLocaleLowerCase().includes(term))
})

async function load(refresh = false): Promise<void>
{
  if (busy.value) { return }
  await run(async () => { report.value = await MediaAPI.storageDetails(refresh) })
}

usePolling(() => load(), 30000)
</script>

<style scoped>
.storage-root { overflow-wrap: anywhere; }
</style>
