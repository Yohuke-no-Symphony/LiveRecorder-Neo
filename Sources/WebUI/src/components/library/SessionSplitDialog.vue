<template>
  <v-dialog v-model="opened" :persistent="busy" max-width="580" scrollable
            :aria-label="t('components.library.splitTitle')">
    <ContentCard :title="t('components.library.splitTitle')">
      <v-radio-group v-model="beforeSegmentId" :label="t('components.library.splitBoundary')" :disabled="busy"
                     hide-details>
        <v-radio v-for="(segment, index) in boundaries" :key="segment.id" :value="segment.id"
                 :label="t('components.library.segment', { number: String(index + 2).padStart(2, '0'), time: formatDuration(segment.startOffsetMs, true), size: formatBytes(segment.committedBytes) })"/>
      </v-radio-group>
      <v-alert v-if="error" type="error" class="mt-5">{{ error }}</v-alert>
      <EmptyState v-if="!boundaries.length" :title="t('components.library.noBoundaries')"/>
      <template #actions>
        <v-btn variant="text" :disabled="busy" @click="opened = false">{{ t('components.common.cancel') }}</v-btn>
        <v-btn color="primary" :loading="busy" :disabled="busy || !beforeSegmentId" @click="submit">{{
          t('components.library.split') }}
        </v-btn>
      </template>
    </ContentCard>
  </v-dialog>
</template>

<script setup lang="ts">
import {useI18n} from 'vue-i18n'
import ContentCard from '@/components/common/ContentCard.vue'
import {computed, ref} from 'vue'
import {MediaAPI} from '@/apis/MediaAPI'
import {useAction} from '@/composables/useAction'
import type {RecordingSegment} from '@/models/media'
import {formatBytes, formatDuration} from '@/utilities/format'
import EmptyState from '@/components/common/EmptyState.vue'

const {t} = useI18n()

const opened = defineModel<boolean>({required: true})
const props = defineProps<{ sessionId: string; segments: RecordingSegment[] }>()
const emit = defineEmits<{ split: [] }>()
const boundaries = computed(() => [...props.segments].sort((a, b) => a.startOffsetMs - b.startOffsetMs).slice(1))
const beforeSegmentId = ref('')
const {busy, error, run} = useAction()

async function submit(): Promise<void> {
  if (busy.value || !boundaries.value.some(segment => segment.id === beforeSegmentId.value)) {
    return
  }
  await run(async () => {
    await MediaAPI.splitSession(props.sessionId, beforeSegmentId.value)
    opened.value = false
    emit('split')
  })
}
</script>
