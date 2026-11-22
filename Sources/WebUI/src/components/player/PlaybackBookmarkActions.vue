<template>
  <div class="bookmark-actions">
    <div class="bookmark-action-buttons">
      <v-btn icon="mdi-bookmark-plus-outline" variant="text" color="primary" size="small"
             :title="t('components.bookmarks.add')" :aria-label="t('components.bookmarks.add')"
             :disabled="unavailable || saving || !editable" @click="emit('bookmark', currentMs)"/>
      <v-btn icon="mdi-ray-start" variant="text" :color="selection.startMs === undefined ? 'white' : 'primary'"
             size="small" :title="t('components.player.setClipStart')" :aria-label="t('components.player.setClipStart')"
             :disabled="unavailable" @click="emit('start', currentMs)"/>
      <v-btn icon="mdi-ray-end" variant="text" :color="selection.endMs === undefined ? 'white' : 'primary'" size="small"
             :title="t('components.player.setClipEnd')" :aria-label="t('components.player.setClipEnd')"
             :disabled="unavailable" @click="emit('end', currentMs)"/>
    </div>
    <div v-if="hasSelection" class="bookmark-selection" :aria-label="t('components.player.selectedClipRange')">
      <span>{{ t('components.player.startTime') }} <b class="mono">{{ selection.startMs === undefined ? '—' : formatTimecode(selection.startMs) }}</b></span>
      <span>{{ t('components.player.endTime') }} <b class="mono">{{ selection.endMs === undefined ? '—' : formatTimecode(selection.endMs) }}</b></span>
      <v-btn icon="mdi-close" variant="text" color="white" size="x-small" :disabled="unavailable"
             :aria-label="t('components.player.clearClipSelection')" @click="emit('clear')"/>
    </div>
    <div v-if="invalid" class="bookmark-selection-error" role="alert">{{ t('components.player.invalidClipRange') }}
    </div>
  </div>
</template>

<script setup lang="ts">
import {computed} from 'vue'
import {useI18n} from 'vue-i18n'
import type {ClipSelection} from '@/models/clipSelection'
import {formatTimecode} from '@/utilities/format'

const props = withDefaults(defineProps<{
  currentMs: number;
  durationMs: number;
  selection: ClipSelection;
  invalid?: boolean;
  saving?: boolean;
  editable?: boolean
}>(), {invalid: false, saving: false, editable: true})
const emit = defineEmits<{
  bookmark: [offsetMs: number];
  start: [offsetMs: number];
  end: [offsetMs: number];
  clear: []
}>()
const {t} = useI18n()
const unavailable = computed(() => props.durationMs <= 0 || !Number.isFinite(props.currentMs))
const hasSelection = computed(() => props.selection.startMs !== undefined || props.selection.endMs !== undefined)
</script>

<style scoped>
.bookmark-actions {
  display: contents;
}

.bookmark-action-buttons {
  display: flex;
  flex-shrink: 0;
  align-items: center;
  gap: 4px;
}

.bookmark-selection {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  order: 1;
  flex-basis: 100%;
  gap: 4px 12px;
  padding: 2px 8px;
  color: #aeb6c4;
  font-size: .68rem;
}

.bookmark-selection b {
  margin-left: 4px;
  color: #edf0f5;
  font-weight: 500;
  white-space: nowrap;
}

.bookmark-selection-error {
  order: 2;
  flex-basis: 100%;
  padding: 2px 8px;
  color: #ffaaa4;
  font-size: .72rem;
}
</style>
