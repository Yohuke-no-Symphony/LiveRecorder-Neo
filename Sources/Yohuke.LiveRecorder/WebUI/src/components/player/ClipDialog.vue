<template>
  <v-dialog v-model="opened" max-width="580">
    <v-card class="pa-6">
      <h2 class="text-h6 mb-6">创建切片</h2>
      <form class="form-stack" @submit.prevent="submit">
        <v-text-field v-model="title" label="名称" required maxlength="180" />
        <v-range-slider v-model="range" :min="0" :max="Math.max(durationMs, 1)" :step="1000" color="primary" hide-details aria-label="切片范围" @update:model-value="syncInputs" />
        <div class="d-flex ga-4"><v-text-field v-model="startInput" label="开始时间" class="mono" placeholder="00:00:00.000" required @blur="updateRange" /><v-text-field v-model="endInput" label="结束时间" class="mono" placeholder="00:00:00.000" required @blur="updateRange" /></div>
        <div class="d-flex justify-space-between align-center"><v-btn-toggle v-model="mode" mandatory color="primary" variant="outlined" density="comfortable"><v-btn value="Exact">精确</v-btn><v-btn value="Fast">快速</v-btn></v-btn-toggle><span class="text-caption mono muted">{{ formatDuration(Math.max(0, range[1]! - range[0]!)) }}</span></div>
        <v-alert v-if="error" type="error">{{ error }}</v-alert>
        <div class="d-flex justify-end ga-3 mt-2"><v-btn variant="text" :disabled="busy" @click="opened = false">取消</v-btn><v-btn type="submit" color="primary" :loading="busy">创建任务</v-btn></div>
      </form>
    </v-card>
  </v-dialog>
</template>

<script setup lang="ts">
import { ref, watch } from 'vue'
import { MediaAPI } from '@/apis/MediaAPI'
import { useAction } from '@/composables/useAction'
import { formatDuration } from '@/utilities/format'
import { parseTimecode } from '@/utilities/timeline'

const opened = defineModel<boolean>({ required: true })
const props = defineProps<{ sessionId: string; sessionTitle: string; currentMs: number; durationMs: number }>()
const emit = defineEmits<{ created: [] }>()
const title = ref('')
const range = ref([0, 30000])
const mode = ref<'Exact' | 'Fast'>('Exact')
const startInput = ref('')
const endInput = ref('')
const { busy, error, run } = useAction()
function inputTime(value: number): string
{
  return `${formatDuration(value, true)}.${String(Math.floor(value % 1000)).padStart(3, '0')}`
}
function syncInputs(): void
{
  startInput.value = inputTime(range.value[0] ?? 0)
  endInput.value = inputTime(range.value[1] ?? 0)
}
function updateRange(): void
{
  const start = parseTimecode(startInput.value)
  const end = parseTimecode(endInput.value)
  if (start !== undefined && end !== undefined)
  {
    range.value = [start, end]
  }
}
watch(opened, value =>
{
  if (value)
  {
    const start = Math.min(Math.max(0, props.currentMs), Math.max(0, props.durationMs - 30000))
    range.value = [start, Math.min(props.durationMs, start + 30000)]
    title.value = props.sessionTitle
    mode.value = 'Exact'
    error.value = ''
    syncInputs()
  }
})
async function submit(): Promise<void>
{
  const startMs = parseTimecode(startInput.value)
  const endMs = parseTimecode(endInput.value)
  if (startMs === undefined || endMs === undefined || startMs < 0 || endMs <= startMs || endMs > props.durationMs)
  {
    error.value = '请输入有效的切片起止时间'
    return
  }
  await run(async () =>
  {
    await MediaAPI.createJob({ kind: 'Clip', sessionId: props.sessionId, title: title.value, mode: mode.value, startMs, endMs })
    opened.value = false
    emit('created')
  })
}
</script>
