<template>
  <v-card>
    <v-table>
      <thead>
        <tr>
          <th>{{ t('views.storage.name') }}</th>
          <th v-for="column in columns" :key="column" class="text-end">{{ t(`views.storage.columns.${column}`) }}</th>
          <th class="text-end"><button class="storage-sort" type="button" @click="descending = !descending">{{ t('views.storage.total') }}<v-icon :icon="descending ? 'mdi-arrow-down' : 'mdi-arrow-up'" size="14" /></button></th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="row in sortedRows" :key="row.id">
          <td class="py-4">
            <div class="d-flex align-center ga-2 flex-wrap">
              <span>{{ row.name || row.id }}</span>
              <v-chip v-if="'retainedInPlace' in row && row.retainedInPlace" size="x-small" color="warning">{{ t('views.storage.retainedInPlace') }}</v-chip>
            </div>
            <div v-if="row.id !== row.name" class="text-caption muted">{{ row.id }}</div>
            <div v-if="row.path" class="storage-path text-caption muted mt-1">{{ row.path }}</div>
          </td>
          <td v-for="column in columns" :key="column" class="text-end text-caption mono storage-size">{{ formatBytes(row[column] ?? 0) }}</td>
          <td class="text-end text-body-2 mono storage-size">{{ formatBytes(row.totalBytes) }}</td>
        </tr>
      </tbody>
    </v-table>
  </v-card>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import type { StorageUsageRow } from '@/models/storage'
import { formatBytes } from '@/utilities/format'

const props = defineProps<{ rows: StorageUsageRow[] }>()
const { t } = useI18n()
const descending = ref(true)
const columns = ['recordingBytes', 'derivedBytes', 'clipBytes', 'trashBytes', 'otherBytes'] as const
const sortedRows = computed(() => [...props.rows].sort((left, right) => (descending.value ? right.totalBytes - left.totalBytes : left.totalBytes - right.totalBytes) || left.id.localeCompare(right.id)))
</script>

<style scoped>
.storage-sort { display: inline-flex; align-items: center; gap: 5px; color: inherit; font: inherit; cursor: pointer; background: none; border: 0; padding: 0; }
.storage-path { overflow-wrap: anywhere; min-width: 180px; max-width: 460px; }
.storage-size { white-space: nowrap; }
</style>
