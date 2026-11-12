<template>
  <div class="storage-block">
    <div class="d-flex justify-space-between align-center mb-3 text-caption">
      <span class="d-flex align-center ga-2"><v-icon icon="mdi-harddisk" size="16" />{{ t('components.storage.disk') }}</span>
      <span class="mono muted">{{ usedPercent }}%</span>
    </div>
    <div class="storage-bar" role="meter" :aria-valuenow="usedPercent" :aria-valuemin="0" :aria-valuemax="100" :aria-label="t('components.storage.usedPercent', { percent: usedPercent })">
      <div class="storage-used" :style="{ width: `${regularPercent}%` }" />
      <div class="storage-trash" :style="{ width: `${trashPercent}%` }" />
    </div>
    <div class="d-flex justify-space-between mt-2 text-caption muted">
      <span>{{ t('components.storage.freeBytes', { bytes: formatBytes(volume.freeBytes) }) }}</span>
      <span>{{ formatBytes(volume.totalBytes) }}</span>
    </div>
    <div v-if="detailed" class="storage-legend mt-4">
      <span><i class="legend-dot bg-primary" />{{ t('components.storage.used', { bytes: formatBytes(regularBytes) }) }}</span>
      <span><i class="legend-dot bg-warning" />{{ t('components.storage.trash', { bytes: formatBytes(volume.trashBytes) }) }}</span>
      <span><i class="legend-dot storage-free" />{{ t('components.storage.free', { bytes: formatBytes(volume.freeBytes) }) }}</span>
    </div>
  </div>
</template>

<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { computed } from 'vue'
import type { StorageVolume } from '@/models/media'
import { formatBytes } from '@/utilities/format'

const { t } = useI18n()

const props = defineProps<{ volume: StorageVolume; detailed?: boolean }>()
const regularBytes = computed(() => Math.max(0, props.volume.totalBytes - props.volume.freeBytes - props.volume.trashBytes))
const percentage = (value: number) => props.volume.totalBytes > 0 ? Math.max(0, Math.min(100, value / props.volume.totalBytes * 100)) : 0
const regularPercent = computed(() => percentage(regularBytes.value))
const trashPercent = computed(() => percentage(props.volume.trashBytes))
const usedPercent = computed(() => Math.round(percentage(props.volume.totalBytes - props.volume.freeBytes)))
</script>

<style scoped>
.storage-bar { height: 7px; border-radius: 6px; overflow: hidden; display: flex; background: rgba(var(--v-theme-on-surface), .09); }
.storage-used { height: 100%; background: rgb(var(--v-theme-primary)); }
.storage-trash { height: 100%; background: rgb(var(--v-theme-warning)); }
.storage-legend { display: flex; gap: 20px; flex-wrap: wrap; font-size: .75rem; color: rgba(var(--v-theme-on-surface), .6); }
.storage-legend span { display: inline-flex; align-items: center; gap: 7px; }
.legend-dot { width: 7px; height: 7px; border-radius: 50%; }
.storage-free { background: rgba(var(--v-theme-on-surface), .15); }
</style>
