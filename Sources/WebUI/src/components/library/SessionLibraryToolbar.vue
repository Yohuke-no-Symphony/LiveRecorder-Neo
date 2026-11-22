<template>
  <div class="selection-toolbar">
    <slot/>
    <template v-if="selectionEnabled">
      <template v-if="selectionMode">
        <span class="text-body-2" role="status">{{ t('components.library.selected', { count: formatNumber(selectedCount) }) }}</span>
        <v-btn variant="text" :disabled="busy || !selectableCount" @click="emit('selectLoaded')">{{
          t('components.library.selectLoaded') }}
        </v-btn>
        <v-btn variant="text" :disabled="busy" @click="emit('cancel')">{{ t('components.library.cancelSelection') }}
        </v-btn>
        <v-spacer/>
        <span class="text-caption muted">{{ t('components.library.selectionLimit', { count: formatNumber(selectionLimit) }) }}</span>
        <v-btn v-if="canTranscode" variant="outlined" prepend-icon="mdi-video-box" :disabled="busy || !selectedCount"
               @click="emit('transcode')">{{ t('transcoding.batchTranscode') }}
        </v-btn>
        <v-btn color="error" variant="outlined" prepend-icon="mdi-delete-outline" :disabled="busy || !selectedCount"
               @click="emit('trash')">{{ t('components.library.trashSelected') }}
        </v-btn>
      </template>
      <v-btn v-else variant="outlined" prepend-icon="mdi-checkbox-multiple-outline" @click="emit('start')">{{
        t('components.library.multiselect') }}
      </v-btn>
    </template>
  </div>
</template>

<script setup lang="ts">
import {useI18n} from 'vue-i18n'
import {formatNumber} from '@/utilities/format'

defineProps<{
  selectionEnabled: boolean
  selectionMode: boolean
  selectedCount: number
  selectableCount: number
  selectionLimit: number
  busy: boolean
  canTranscode?: boolean
}>()
const emit = defineEmits<{ start: []; selectLoaded: []; cancel: []; trash: []; transcode: [] }>()
const {t} = useI18n()
</script>

<style scoped>
.selection-toolbar {
  display: flex;
  flex-wrap: wrap;
  gap: 8px 12px;
  align-items: center;
}
</style>
