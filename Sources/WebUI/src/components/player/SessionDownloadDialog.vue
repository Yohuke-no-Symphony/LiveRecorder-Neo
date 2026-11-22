<template>
  <v-dialog v-model="opened" max-width="720" scrollable :aria-label="t('components.download.title')">
    <ContentCard :title="t('components.download.title')">
      <template #header-actions>
        <v-btn icon="mdi-close" variant="text" size="small" :aria-label="t('components.download.close')"
               @click="opened = false"/>
      </template>
      <p class="text-body-2 mb-1">{{ sessionTitle }}</p>
      <p class="text-caption muted mb-4">{{ t('components.download.description') }}</p>
      <div class="download-selection">
        <v-checkbox :model-value="allSelected" :indeterminate="partiallySelected"
                    :disabled="!downloadableSegments.length" :label="t('components.download.selectAll')"
                    density="compact" hide-details @update:model-value="selectAll"/>
        <span class="text-caption mono muted">{{ t('components.download.selected', { count: formatNumber(selectedSegments.length), size: formatBytes(selectedBytes) }) }}</span>
      </div>
      <div v-for="(segment, index) in segments" :key="segment.id" class="download-segment">
        <v-checkbox v-model="selectedIds" :value="segment.id" :disabled="!canDownload(segment)"
                    :aria-label="t('components.download.selectSegment', { number: partNumber(index) })"
                    density="compact" hide-details class="download-selector"/>
        <div class="download-segment-info">
          <div class="text-body-2">{{ t('components.download.segment', { number: partNumber(index) }) }}</div>
          <div class="text-caption mono muted mt-1">{{ t('components.download.timeRange', { start:
            formatDuration(segment.startOffsetMs, true), duration: formatDuration(segment.durationMs) }) }} · {{
            formatBytes(segment.committedBytes) }}
          </div>
          <div v-if="!canDownload(segment)" class="text-caption muted mt-1">{{ t(segment.isClosed ?
            'components.download.unavailable' : 'components.download.recording') }}
          </div>
        </div>
        <v-btn :href="canDownload(segment) ? MediaAPI.downloadSegment(sessionId, segment.id) : undefined"
               :disabled="!canDownload(segment)"
               :aria-label="t('components.download.downloadSegment', { number: partNumber(index) })" download
               variant="text" prepend-icon="mdi-download" size="small">{{ t('components.download.single') }}
        </v-btn>
      </div>
      <EmptyState v-if="!segments.length" :title="t('components.download.empty')" icon="mdi-download-off-outline"/>
      <p v-else-if="!downloadableSegments.length" class="text-body-2 muted mt-4">{{ t('components.download.empty')
        }}</p>
      <p v-if="requestedCount" role="status" aria-live="polite" class="text-caption muted mt-4">{{
        t('components.download.requested', { count: formatNumber(requestedCount) }) }}</p>
      <template #actions>
        <v-btn variant="text" @click="opened = false">{{ t('components.common.cancel') }}</v-btn>
        <v-btn color="primary" prepend-icon="mdi-download" :disabled="!selectedSegments.length" data-download-selected
               @click="downloadSelected">{{ t('components.download.selectedAction') }}
        </v-btn>
      </template>
    </ContentCard>
  </v-dialog>
</template>

<script setup lang="ts">
import {computed, ref, watch} from 'vue'
import {useI18n} from 'vue-i18n'
import {MediaAPI} from '@/apis/MediaAPI'
import type {RecordingSegment} from '@/models/media'
import {formatBytes, formatDuration, formatNumber} from '@/utilities/format'
import ContentCard from '@/components/common/ContentCard.vue'
import EmptyState from '@/components/common/EmptyState.vue'

const opened = defineModel<boolean>({required: true})
const props = defineProps<{ sessionId: string; sessionTitle: string; segments: RecordingSegment[] }>()
const {t} = useI18n()
const selectedIds = ref<string[]>([])
const requestedCount = ref(0)
const downloadableSegments = computed(() => props.segments.filter(canDownload))
const selectedSegments = computed(() => downloadableSegments.value.filter(segment => selectedIds.value.includes(segment.id)))
const selectedBytes = computed(() => selectedSegments.value.reduce((total, segment) => total + segment.committedBytes, 0))
const allSelected = computed(() => downloadableSegments.value.length > 0 && selectedSegments.value.length === downloadableSegments.value.length)
const partiallySelected = computed(() => selectedSegments.value.length > 0 && !allSelected.value)

function canDownload(segment: RecordingSegment): boolean {
  return segment.isClosed && segment.durationMs > 0 && segment.committedBytes >= 13
}

function partNumber(index: number): string {
  return String(index + 1).padStart(2, '0')
}

function selectAll(value: boolean | null): void {
  selectedIds.value = value ? downloadableSegments.value.map(segment => segment.id) : []
}

watch([opened, () => props.sessionId], ([value]) => {
  if (value) {
    selectAll(true)
    requestedCount.value = 0
  }
}, {immediate: true})

watch(downloadableSegments, segments => {
  const available = new Set(segments.map(segment => segment.id))
  selectedIds.value = selectedIds.value.filter(id => available.has(id))
})

function downloadSelected(): void {
  const segments = selectedSegments.value
  if (!segments.length) {
    return
  }
  for (const segment of segments) {
    const link = document.createElement('a')
    link.href = MediaAPI.downloadSegment(props.sessionId, segment.id)
    link.download = ''
    link.hidden = true
    document.body.append(link)
    try {
      link.click()
    } finally {
      link.remove()
    }
  }
  requestedCount.value = segments.length
}
</script>

<style scoped>
.download-selection {
  display: flex;
  align-items: center;
  justify-content: space-between;
  flex-wrap: wrap;
  gap: 8px 16px;
}

.download-selection > :first-child {
  flex: 0 1 auto;
}

.download-segment {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 12px 0;
  border-bottom: 1px solid rgba(var(--v-theme-on-surface), .06);
}

.download-segment:last-of-type {
  border-bottom: 0;
}

.download-selector {
  flex: 0 0 40px;
}

.download-segment-info {
  flex: 1;
  min-width: 0;
}
</style>
