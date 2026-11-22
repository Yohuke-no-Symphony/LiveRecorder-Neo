<template>
  <v-card>
    <v-data-table
      v-model:page="page"
      v-model:sort-by="sortBy"
      :headers="headers"
      :items="rows"
      :items-per-page="25"
      :items-per-page-options="[25, 50, 100]"
      :aria-label="t('views.storage.title')"
      item-value="id"
      must-sort
    >
      <template #item.name="{ item: row }">
        <div class="d-flex align-center ga-2 flex-wrap">
          <span>{{ row.name || row.id }}</span>
          <v-chip v-if="'retainedInPlace' in row && row.retainedInPlace" size="x-small" color="warning">{{
            t('views.storage.retainedInPlace') }}
          </v-chip>
        </div>
        <div v-if="row.id !== row.name" class="text-caption muted">{{ row.id }}</div>
        <div v-if="row.path" class="storage-path text-caption muted mt-1">{{ row.path }}</div>
      </template>
      <template v-for="column in byteColumns" :key="column" #[`item.${column}`]="{ value }">{{ formatBytes(value) }}
      </template>
    </v-data-table>
  </v-card>
</template>

<script setup lang="ts">
import {computed, ref} from 'vue'
import {useI18n} from 'vue-i18n'
import type {StorageUsageRow} from '@/models/storage'
import {formatBytes} from '@/utilities/format'

defineProps<{ rows: StorageUsageRow[] }>()
const page = defineModel<number>('page', {required: true})
const {t} = useI18n()
const sortBy = ref<readonly { key: string; order?: 'asc' | 'desc' | boolean }[]>([{key: 'totalBytes', order: 'desc'}])
const byteColumns = ['recordingBytes', 'derivedBytes', 'clipBytes', 'trashBytes', 'otherBytes', 'totalBytes'] as const
const headers = computed(() => [
  {
    key: 'name',
    title: t('views.storage.name'),
    value: (row: StorageUsageRow) => row.name || row.id,
    cellProps: {class: 'py-5'}
  },
  ...byteColumns.map(key => ({
    key,
    title: t(key === 'totalBytes' ? 'views.storage.total' : `views.storage.columns.${key}`),
    value: (row: StorageUsageRow) => row[key] ?? 0,
    align: 'end' as const,
    nowrap: true,
    cellProps: {class: key === 'totalBytes' ? 'text-body-2 mono' : 'text-caption mono'},
  })),
])
</script>

<style scoped>
.storage-path {
  overflow-wrap: anywhere;
  min-width: 180px;
  max-width: 460px;
}
</style>
