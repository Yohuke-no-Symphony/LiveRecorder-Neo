<template>
  <v-dialog v-model="opened" max-width="580" scrollable :persistent="busy" :aria-label="t('liveMarkers.uploadTitle')">
    <ContentCard :title="t('liveMarkers.uploadTitle')">
      <form :id="formId" class="form-stack" @submit.prevent="submit">
        <v-file-input v-model="selectedFile" :label="t('liveMarkers.file')" accept=".yaml,.yml" :hint="t('liveMarkers.fileHint')" persistent-hint required :disabled="busy" />
        <v-text-field v-model="timeZoneId" :label="t('liveMarkers.sourceTimeZone')" :hint="t('liveMarkers.timeZoneHint')" persistent-hint required :disabled="busy" />
        <v-text-field v-model="calibrationSeconds" :label="t('liveMarkers.calibration')" :hint="t('liveMarkers.calibrationHint')" persistent-hint type="number" step="0.001" required :disabled="busy" />
        <v-alert v-if="validationError || error" type="error">{{ validationError || error }}</v-alert>
      </form>
      <template #actions>
        <v-btn variant="text" :disabled="busy" @click="opened = false">{{ t('liveMarkers.cancel') }}</v-btn>
        <v-btn type="submit" :form="formId" color="primary" :loading="busy">{{ t('liveMarkers.upload') }}</v-btn>
      </template>
    </ContentCard>
  </v-dialog>
</template>

<script setup lang="ts">
import { ref, useId, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import type { LiveMarkerUpload } from '@/models/liveMarkers'
import { parseLiveMarkerCalibration } from '@/utilities/liveMarkers'
import ContentCard from '@/components/common/ContentCard.vue'

const opened = defineModel<boolean>({ required: true })
const props = defineProps<{ defaultTimeZoneId: string; busy: boolean; error: string }>()
const emit = defineEmits<{ upload: [value: LiveMarkerUpload] }>()
const { t } = useI18n()
const formId = useId()
const selectedFile = ref<File | File[] | null>(null)
const timeZoneId = ref('')
const calibrationSeconds = ref<number | string>(0)
const validationError = ref('')

watch(opened, value =>
{
  if (value)
  {
    selectedFile.value = null
    timeZoneId.value = props.defaultTimeZoneId
    calibrationSeconds.value = 0
    validationError.value = ''
  }
}, { immediate: true })

function submit(): void
{
  if (props.busy) { return }
  validationError.value = ''
  const file = Array.isArray(selectedFile.value) ? selectedFile.value[0] : selectedFile.value
  if (!file || !/\.ya?ml$/i.test(file.name) || file.size > 5 * 1024 * 1024)
  {
    validationError.value = t('liveMarkers.invalidFile')
    return
  }
  const calibrationMs = parseLiveMarkerCalibration(calibrationSeconds.value)
  if (calibrationMs === undefined)
  {
    validationError.value = t('liveMarkers.invalidCalibration')
    return
  }
  emit('upload', { file, timeZoneId: timeZoneId.value.trim(), calibrationMs })
}
</script>
