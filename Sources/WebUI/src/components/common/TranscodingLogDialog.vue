<template>
  <v-dialog :model-value="true" max-width="1000" scrollable :aria-label="title"
            @update:model-value="!$event && emit('close')">
    <ContentCard :title="title" class="transcoding-log-dialog">
      <WorkerLogViewer :entries="entries" :loading="loading" :error="error" :retrying="retrying" :title="title"
                       :stream-key="worker.id"/>
      <template #actions>
        <v-btn variant="text" @click="emit('close')">{{ t('transcoding.close') }}</v-btn>
      </template>
    </ContentCard>
  </v-dialog>
</template>

<script setup lang="ts">
import {computed, watch} from 'vue'
import {useI18n} from 'vue-i18n'
import {useTranscodingLogs} from '@/composables/useTranscodingLogs'
import type {Worker} from '@/models/transcoding'
import {useAuthStore} from '@/stores/auth'
import ContentCard from './ContentCard.vue'
import WorkerLogViewer from './WorkerLogViewer.vue'

const props = defineProps<{ worker: Pick<Worker, 'id' | 'name'> }>()
const emit = defineEmits<{ close: [] }>()
const auth = useAuthStore()
const {t} = useI18n()
const {entries, loading, error, retrying} = useTranscodingLogs(() => props.worker.id)
const title = computed(() => t('transcoding.workerLogs', {name: props.worker.name}))
watch(() => auth.isAdmin, allowed => {
  if (!allowed) {
    emit('close')
  }
}, {immediate: true, flush: 'sync'})
</script>
