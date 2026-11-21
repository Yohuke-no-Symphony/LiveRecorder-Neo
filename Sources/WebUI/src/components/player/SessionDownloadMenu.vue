<template>
  <v-menu location="bottom end">
    <template #activator="{ props: menuProps }">
      <v-tooltip :text="t('components.download.open')" location="top">
        <template #activator="{ props: tooltipProps }">
          <v-btn v-bind="mergeProps(menuProps, tooltipProps)" icon="mdi-download" size="40" variant="tonal" :aria-label="t('components.download.open')" :loading="!!activeDownload" />
        </template>
      </v-tooltip>
    </template>
    <v-list density="compact">
      <v-list-item role="menuitem" prepend-icon="mdi-video-outline" :title="t('components.download.video')" @click="emit('video')" />
      <v-list-item role="menuitem" prepend-icon="mdi-message-text-outline" :title="t('components.download.danmu')" :disabled="!!activeDownload" @click="downloadText(false)" />
      <v-list-item role="menuitem" prepend-icon="mdi-translate" :title="t('components.download.translations')" :disabled="!!activeDownload" @click="downloadText(true)" />
    </v-list>
  </v-menu>
  <v-snackbar :model-value="!!error" color="error" @update:model-value="!$event && (error = '')">{{ error }}</v-snackbar>
</template>

<script setup lang="ts">
import { mergeProps, onScopeDispose, ref, shallowRef, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { MediaAPI } from '@/apis/MediaAPI'
import { saveDownloadedFile } from '@/utilities/downloadFile'

const props = defineProps<{ sessionId: string }>()
const emit = defineEmits<{ video: [] }>()
const { t } = useI18n()
const activeDownload = shallowRef<AbortController>()
const error = ref('')

function cancelDownload(): void
{
  activeDownload.value?.abort()
  activeDownload.value = undefined
  error.value = ''
}

async function downloadText(translationsOnly: boolean): Promise<void>
{
  if (activeDownload.value)
  {
    return
  }
  const controller = new AbortController()
  activeDownload.value = controller
  error.value = ''
  try
  {
    const file = await MediaAPI.downloadSessionEvents(props.sessionId, translationsOnly, controller.signal)
    if (!controller.signal.aborted)
    {
      saveDownloadedFile(file)
    }
  }
  catch (cause)
  {
    if (!controller.signal.aborted)
    {
      error.value = cause instanceof Error ? cause.message : t('components.common.operationFailed')
    }
  }
  finally
  {
    if (activeDownload.value === controller)
    {
      activeDownload.value = undefined
    }
  }
}

watch(() => props.sessionId, cancelDownload, { flush: 'sync' })
onScopeDispose(cancelDownload)
</script>
