<template>
  <div class="analytics-export">
    <v-tooltip :text="rangeLabel" location="bottom">
      <template #activator="{ props: tooltipProps }">
        <v-btn v-bind="tooltipProps" class="analytics-export-current" variant="tonal"
               prepend-icon="mdi-file-excel-outline" :loading="busy" :disabled="unavailable"
               :aria-label="`${t('analyticsExport.action')} · ${rangeLabel}`" @click="exportFile(range)">{{
          t('analyticsExport.action') }}
        </v-btn>
      </template>
    </v-tooltip>
    <v-menu v-if="range" location="bottom end">
      <template #activator="{ props: menuProps }">
        <v-btn v-bind="menuProps" class="analytics-export-options" variant="tonal" icon="mdi-chevron-down"
               :disabled="unavailable" :aria-label="t('analyticsExport.options')"/>
      </template>
      <v-list density="compact">
        <v-list-item :title="t('analyticsExport.fullSession')" :subtitle="fullRangeLabel"
                     prepend-icon="mdi-file-excel-outline" :disabled="unavailable" role="menuitem"
                     @click="exportFile()"/>
      </v-list>
    </v-menu>
    <v-snackbar :model-value="Boolean(error)" color="error" @update:model-value="error = $event ? error : ''">{{ error
      }}
    </v-snackbar>
  </div>
</template>

<script setup lang="ts">
import {computed, onScopeDispose, ref, watch} from 'vue'
import {useI18n} from 'vue-i18n'
import {AnalyticsExportAPI} from '@/apis/AnalyticsExportAPI'
import type {AnalyticsRange} from '@/models/analyticsRange'
import {formatTimecode} from '@/utilities/format'
import {saveDownloadedFile} from '@/utilities/downloadFile'

const props = defineProps<{ sessionId: string; range?: AnalyticsRange; durationMs: number; disabled?: boolean }>()
const {t} = useI18n()
const busy = ref(false)
const error = ref('')
const unavailable = computed(() => props.disabled || busy.value || !props.sessionId)
const fullRangeLabel = computed(() => `${timecode(0)} — ${timecode(props.durationMs)}`)
const rangeLabel = computed(() => props.range
  ? t('analyticsExport.current', {from: timecode(props.range.fromMs), to: timecode(props.range.toMs)})
  : `${t('analyticsExport.fullSession')} · ${fullRangeLabel.value}`)
let controller: AbortController | undefined
let generation = 0

function timecode(value: number): string {
  return formatTimecode(value).replace(/\.000$/, '')
}

function cancelExport(): void {
  generation++
  controller?.abort()
  controller = undefined
  busy.value = false
  error.value = ''
}

async function exportFile(range?: AnalyticsRange): Promise<void> {
  if (unavailable.value) {
    return
  }
  const requestGeneration = ++generation
  const requestController = new AbortController()
  controller = requestController
  busy.value = true
  error.value = ''
  try {
    const file = await AnalyticsExportAPI.download(props.sessionId, range, requestController.signal)
    if (requestGeneration === generation && !requestController.signal.aborted) {
      saveDownloadedFile(file)
    }
  } catch (cause) {
    if (requestGeneration === generation && !requestController.signal.aborted) {
      error.value = cause instanceof Error ? cause.message : t('analyticsExport.failed')
    }
  } finally {
    if (requestGeneration === generation) {
      controller = undefined
      busy.value = false
    }
  }
}

watch(() => props.sessionId, cancelExport, {flush: 'sync'})
onScopeDispose(cancelExport)
</script>

<style scoped>
.analytics-export {
  display: inline-flex;
  align-items: center;
  flex: 0 0 auto;
  gap: 2px;
}

.analytics-export-options {
  width: 30px;
  height: 36px;
  border-radius: 8px;
}
</style>
