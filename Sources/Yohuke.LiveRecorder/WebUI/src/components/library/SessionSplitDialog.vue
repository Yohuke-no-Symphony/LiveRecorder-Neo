<template>
  <v-dialog v-model="opened" :persistent="busy" max-width="580" scrollable>
    <v-card>
      <h2 class="text-h6 pa-6">拆分场次</h2>
      <v-card-text class="pt-0"><v-radio-group v-model="beforeSegmentId" label="拆分起始片段" :disabled="busy" hide-details><v-radio v-for="(segment, index) in boundaries" :key="segment.id" :value="segment.id" :label="`片段 ${String(index + 2).padStart(2, '0')} · ${formatDuration(segment.startOffsetMs, true)} · ${formatBytes(segment.committedBytes)}`" /></v-radio-group><v-alert v-if="error" type="error" class="mt-5">{{ error }}</v-alert><EmptyState v-if="!boundaries.length" title="暂无可拆分片段" /></v-card-text>
      <div class="d-flex justify-end ga-3 pa-6"><v-btn variant="text" :disabled="busy" @click="opened = false">取消</v-btn><v-btn color="primary" :loading="busy" :disabled="busy || !beforeSegmentId" @click="submit">拆分</v-btn></div>
    </v-card>
  </v-dialog>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue'
import { MediaAPI } from '@/apis/MediaAPI'
import { useAction } from '@/composables/useAction'
import type { RecordingSegment } from '@/models/media'
import { formatBytes, formatDuration } from '@/utilities/format'
import EmptyState from '@/components/common/EmptyState.vue'

const opened = defineModel<boolean>({ required: true })
const props = defineProps<{ sessionId: string; segments: RecordingSegment[] }>()
const emit = defineEmits<{ split: [] }>()
const boundaries = computed(() => [...props.segments].sort((a, b) => a.startOffsetMs - b.startOffsetMs).slice(1))
const beforeSegmentId = ref('')
const { busy, error, run } = useAction()
async function submit(): Promise<void>
{
  if (busy.value || !boundaries.value.some(segment => segment.id === beforeSegmentId.value)) { return }
  await run(async () =>
  {
    await MediaAPI.splitSession(props.sessionId, beforeSegmentId.value)
    opened.value = false
    emit('split')
  })
}
</script>
