<template>
  <v-tooltip :disabled="detailed" location="top">
    <template #activator="{ props: tooltipProps }">
      <div v-bind="detailed ? {} : tooltipProps" class="storage-block" :tabindex="detailed ? undefined : 0">
        <div class="d-flex justify-space-between align-center mb-3 text-caption">
          <span class="d-flex align-center ga-2"><v-icon icon="mdi-harddisk" size="16" />{{ title ?? t('components.storage.recordingSpace') }}</span>
          <span class="mono muted">{{ usedPercent }}%</span>
        </div>
        <div class="storage-bar" role="meter" :aria-valuenow="usedPercent" :aria-valuemin="0" :aria-valuemax="100" :aria-label="t('components.storage.usedPercent', { percent: usedPercent })">
          <div v-for="segment in segments" :key="segment.id" class="storage-segment" :class="`storage-${segment.id}`" :data-category="segment.id" :style="{ width: `${segment.percent}%` }" :title="segmentLabel(segment)" />
        </div>
        <div class="d-flex justify-space-between mt-2 text-caption muted ga-2">
          <span>{{ t('components.storage.freeBytes', { bytes: formatBytes(volume.freeBytes) }) }}</span>
          <span>{{ formatBytes(volume.totalBytes) }}</span>
        </div>
        <div v-if="detailed" class="storage-legend mt-4">
          <span v-for="segment in segments" :key="segment.id"><i class="legend-dot" :class="`storage-${segment.id}`" />{{ segmentLabel(segment) }}</span>
        </div>
      </div>
    </template>
    <div class="storage-legend storage-tooltip">
      <span v-for="segment in segments" :key="segment.id"><i class="legend-dot" :class="`storage-${segment.id}`" />{{ segmentLabel(segment) }}</span>
    </div>
  </v-tooltip>
</template>

<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { computed } from 'vue'
import type { StorageVolume } from '@/models/media'
import { formatBytes } from '@/utilities/format'
import { storageSegments, type StorageSegment } from '@/utilities/storage'

const { t } = useI18n()
const props = defineProps<{ volume: StorageVolume; detailed?: boolean; title?: string }>()
const segments = computed(() => storageSegments(props.volume))
const usedPercent = computed(() => props.volume.totalBytes > 0 ? Math.round(100 - (segments.value.find(segment => segment.id === 'free')?.percent ?? 100)) : 0)
const segmentLabel = (segment: StorageSegment) => t(`components.storage.${segment.id}`, { bytes: formatBytes(segment.bytes) })
</script>

<style scoped>
.storage-bar { height: 7px; border-radius: 6px; overflow: hidden; display: flex; background: rgba(var(--v-theme-on-surface), .09); }
.storage-segment { height: 100%; flex-shrink: 0; }
.storage-recordings { background: rgb(var(--v-theme-primary)); }
.storage-clips { background: rgb(var(--v-theme-info)); }
.storage-trash { background: rgb(var(--v-theme-warning)); }
.storage-other { background: rgb(var(--v-theme-secondary)); opacity: .65; }
.storage-free { background: rgba(var(--v-theme-on-surface), .15); }
.storage-legend { display: flex; gap: 10px 20px; flex-wrap: wrap; font-size: .75rem; color: rgba(var(--v-theme-on-surface), .6); }
.storage-legend span { display: inline-flex; align-items: center; gap: 7px; }
.legend-dot { width: 7px; height: 7px; border-radius: 50%; flex-shrink: 0; }
.storage-tooltip { flex-direction: column; gap: 8px; color: inherit; }
</style>
