<template>
  <v-dialog :model-value="true" max-width="640" scrollable :aria-labelledby="titleId" @update:model-value="!$event && emit('close')">
    <ContentCard :title="title" :title-id="titleId" :aria-busy="loading">
      <template #header-actions>
        <v-btn icon="mdi-refresh" variant="text" :loading="loading" :aria-label="t('transcoding.refresh')" @click="refresh" />
      </template>
      <v-progress-linear v-if="loading" indeterminate color="primary" :aria-label="t('transcoding.assignmentsLoading')" class="mb-3" />
      <v-alert v-if="error" type="error" density="compact" class="mb-3">{{ error }}</v-alert>
      <p v-if="!assignments.length && !loading && !error" class="muted">{{ t('transcoding.assignmentsEmpty') }}</p>
      <ul v-if="assignments.length" class="assignment-list">
        <li v-for="assignment in assignments" :key="assignment.roomId" class="assignment-row">
          <UserAvatar :name="assignment.name || String(assignment.roomId)" :avatar-url="MediaAPI.avatar(assignment.targetId)" :size="40" />
          <span class="assignment-name font-weight-medium">{{ assignment.name || assignment.roomId }}</span>
          <span class="mono muted" :aria-label="t('components.library.room', { roomId: assignment.roomId })">{{ assignment.roomId }}</span>
        </li>
      </ul>
      <template #actions>
        <v-btn variant="text" @click="emit('close')">{{ t('transcoding.close') }}</v-btn>
      </template>
    </ContentCard>
  </v-dialog>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, ref, useId, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { MediaAPI } from '@/apis/MediaAPI'
import { TranscodingAPI } from '@/apis/TranscodingAPI'
import type { RecordingWorkerAssignment } from '@/models/transcoding'
import ContentCard from './ContentCard.vue'
import UserAvatar from './UserAvatar.vue'

const props = defineProps<{ worker: { id: string; name: string } }>()
const emit = defineEmits<{ close: [] }>()
const { t } = useI18n()
const titleId = useId()
const title = computed(() => t('transcoding.workerAssignments', { name: props.worker.name }))
const assignments = ref<RecordingWorkerAssignment[]>([])
const loading = ref(false)
const error = ref('')
let disposed = false
let requestRevision = 0
let controller: AbortController | undefined

async function refresh(): Promise<void>
{
  const revision = ++requestRevision
  controller?.abort()
  controller = new AbortController()
  loading.value = true
  error.value = ''
  try
  {
    const result = await TranscodingAPI.workerAssignments(props.worker.id, controller.signal)
    if (!disposed && revision === requestRevision)
    {
      assignments.value = result
    }
  }
  catch (value)
  {
    if (!disposed && revision === requestRevision)
    {
      error.value = value instanceof Error ? value.message : t('transcoding.assignmentsLoadFailed')
    }
  }
  finally
  {
    if (!disposed && revision === requestRevision)
    {
      loading.value = false
    }
  }
}

watch(() => props.worker.id, () =>
{
  assignments.value = []
  void refresh()
}, { immediate: true })

onBeforeUnmount(() =>
{
  disposed = true
  requestRevision++
  controller?.abort()
})
</script>

<style scoped>
.assignment-list { margin: 0; padding: 0; list-style: none; }
.assignment-row { display: grid; grid-template-columns: 40px minmax(0, 1fr) auto; align-items: center; gap: 12px; padding: 12px 0; border-bottom: 1px solid rgba(var(--v-theme-on-surface), .08); }
.assignment-row:last-child { border-bottom: 0; }
.assignment-name { min-width: 0; overflow-wrap: anywhere; }
</style>
