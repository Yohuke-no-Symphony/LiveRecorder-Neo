<template>
  <v-dialog v-model="opened" max-width="500" :persistent="busy" :aria-label="t('liveMarkers.alignmentTitle')">
    <ContentCard :title="t('liveMarkers.alignmentTitle')">
      <form :id="formId" class="form-stack" @submit.prevent="submit">
        <p class="text-body-2 text-break">{{ file?.originalFileName }}</p>
        <v-text-field v-model="calibrationSeconds" :label="t('liveMarkers.calibration')" :hint="t('liveMarkers.calibrationHint')" persistent-hint type="number" step="0.001" required :disabled="busy" />
        <v-alert v-if="validationError || error" type="error">{{ validationError || error }}</v-alert>
      </form>
      <template #actions>
        <v-btn variant="text" :disabled="busy" @click="opened = false">{{ t('liveMarkers.cancel') }}</v-btn>
        <v-btn type="submit" :form="formId" color="primary" :loading="busy">{{ t('liveMarkers.save') }}</v-btn>
      </template>
    </ContentCard>
  </v-dialog>
</template>

<script setup lang="ts">
import { ref, useId, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import type { LiveMarkerFile } from '@/models/liveMarkers'
import { parseLiveMarkerCalibration } from '@/utilities/liveMarkers'
import ContentCard from '@/components/common/ContentCard.vue'

const opened = defineModel<boolean>({ required: true })
const props = defineProps<{ file?: LiveMarkerFile; busy: boolean; error: string }>()
const emit = defineEmits<{ save: [calibrationMs: number] }>()
const { t } = useI18n()
const formId = useId()
const calibrationSeconds = ref<number | string>(0)
const validationError = ref('')

watch(opened, value =>
{
  if (value)
  {
    calibrationSeconds.value = (props.file?.calibrationMs ?? 0) / 1000
    validationError.value = ''
  }
}, { immediate: true })

function submit(): void
{
  if (props.busy) { return }
  const calibrationMs = parseLiveMarkerCalibration(calibrationSeconds.value)
  if (calibrationMs === undefined)
  {
    validationError.value = t('liveMarkers.invalidCalibration')
    return
  }
  validationError.value = ''
  emit('save', calibrationMs)
}
</script>
