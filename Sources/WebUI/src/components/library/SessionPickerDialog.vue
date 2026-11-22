<template>
  <v-dialog v-model="open" max-width="1180" scrollable :aria-label="t('views.analytics.selectRecording')">
    <v-card class="session-picker-dialog">
      <CardHeader :title="t('views.analytics.selectRecording')">
        <template #actions>
          <v-btn type="button" icon="mdi-close" variant="text" size="small" :aria-label="t('views.common.close')"
                 @click="open = false"/>
        </template>
      </CardHeader>
      <v-card-text class="session-picker-body">
        <SessionPickerLibrary v-if="open" :selected-id="selectedId" @pick="select"/>
      </v-card-text>
    </v-card>
  </v-dialog>
</template>

<script setup lang="ts">
import {useI18n} from 'vue-i18n'
import CardHeader from '@/components/common/CardHeader.vue'
import SessionPickerLibrary from './SessionPickerLibrary.vue'

defineProps<{ selectedId: string }>()
const open = defineModel<boolean>({required: true})
const emit = defineEmits<{ select: [id: string] }>()
const {t} = useI18n()

function select(id: string): void {
  emit('select', id)
  open.value = false
}
</script>

<style scoped>
.session-picker-dialog {
  max-height: min(90dvh, 1000px);
}

.session-picker-body {
  min-height: 0;
  padding: 0 var(--ui-card-padding) var(--ui-card-padding);
}
</style>
