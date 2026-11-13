<template>
  <v-dialog v-model="opened" max-width="580" :aria-label="t('components.player.createClip')">
    <ContentCard :title="t('components.player.createClip')">
      <form class="form-stack" @submit.prevent="submit">
        <v-text-field v-model="title" :label="t('components.player.name')" required maxlength="180" />
        <div v-if="bookmarks?.length" class="clip-bookmark-inputs"><v-select :model-value="startBookmarkId" :items="bookmarkItems" item-title="label" item-value="id" :label="t('components.bookmarks.startSelect')" clearable hide-details @update:model-value="applyBookmark('start', $event)" /><v-select :model-value="endBookmarkId" :items="bookmarkItems" item-title="label" item-value="id" :label="t('components.bookmarks.endSelect')" clearable hide-details @update:model-value="applyBookmark('end', $event)" /></div>
        <v-range-slider :model-value="sliderRange" :min="0" :max="Math.max(durationMs, 1)" :step="1" color="primary" hide-details :aria-label="t('components.player.clipRange')" @update:model-value="updateSlider" />
        <div class="form-row"><v-text-field v-model="startInput" :label="t('components.player.startTime')" class="mono" placeholder="00:00:00.000" required @blur="updateRange" /><v-text-field v-model="endInput" :label="t('components.player.endTime')" class="mono" placeholder="00:00:00.000" required @blur="updateRange" /></div>
        <div class="d-flex justify-space-between align-center"><v-btn-toggle v-model="mode" mandatory color="primary" variant="outlined" density="comfortable"><v-btn value="Exact">{{ t('components.player.exact') }}</v-btn><v-btn value="Fast">{{ t('components.player.fast') }}</v-btn></v-btn-toggle><span class="text-caption mono muted">{{ formatDuration(Math.max(0, range[1]! - range[0]!)) }}</span></div>
        <v-alert v-if="error" type="error">{{ error }}</v-alert>
        <CardActions embedded><v-btn variant="text" :disabled="busy" @click="opened = false">{{ t('components.common.cancel') }}</v-btn><v-btn type="submit" color="primary" :loading="busy">{{ t('components.player.createJob') }}</v-btn></CardActions>
      </form>
    </ContentCard>
  </v-dialog>
</template>

<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import ContentCard from '@/components/common/ContentCard.vue'
import CardActions from '@/components/common/CardActions.vue'
import { computed, ref, watch } from 'vue'
import { MediaAPI } from '@/apis/MediaAPI'
import { useAction } from '@/composables/useAction'
import type { ClipSelection } from '@/models/clipSelection'
import type { SessionBookmark } from '@/models/sessionBookmark'
import { formatDuration, formatTimecode } from '@/utilities/format'
import { parseTimecode } from '@/utilities/timeline'
import { resolveClipRange } from '@/utilities/clipSelection'

const { t } = useI18n()

const opened = defineModel<boolean>({ required: true })
const props = defineProps<{ sessionId: string; sessionTitle: string; currentMs: number; durationMs: number; selection?: ClipSelection; bookmarks?: SessionBookmark[] }>()
const emit = defineEmits<{ created: [] }>()
const title = ref('')
const range = ref([0, 30000])
const mode = ref<'Exact' | 'Fast'>('Exact')
const startInput = ref('')
const endInput = ref('')
const startBookmarkId = ref<string>()
const endBookmarkId = ref<string>()
const bookmarkItems = computed(() => (props.bookmarks ?? []).map(bookmark => ({ id: bookmark.id, label: `${formatTimecode(bookmark.offsetMs)} · ${bookmark.title}` })))
const sliderRange = computed(() => [Math.min(range.value[0] ?? 0, range.value[1] ?? 0), Math.max(range.value[0] ?? 0, range.value[1] ?? 0)])
let suggestedTitle = ''
const { busy, error, run } = useAction()
function syncInputs(): void
{
  startInput.value = formatTimecode(range.value[0] ?? 0)
  endInput.value = formatTimecode(range.value[1] ?? 0)
  if (props.bookmarks?.find(item => item.id === startBookmarkId.value)?.offsetMs !== range.value[0]) { startBookmarkId.value = undefined }
  if (props.bookmarks?.find(item => item.id === endBookmarkId.value)?.offsetMs !== range.value[1]) { endBookmarkId.value = undefined }
}
function updateSlider(value: number[]): void
{
  range.value = value
  syncInputs()
}
function applyBookmark(boundary: 'start' | 'end', id: string | null): void
{
  if (boundary === 'start') { startBookmarkId.value = id ?? undefined }
  else { endBookmarkId.value = id ?? undefined }
  const bookmark = props.bookmarks?.find(item => item.id === id)
  if (!bookmark) { return }
  range.value = boundary === 'start' ? [bookmark.offsetMs, parseTimecode(endInput.value) ?? range.value[1] ?? 0] : [parseTimecode(startInput.value) ?? range.value[0] ?? 0, bookmark.offsetMs]
  if (boundary === 'start' && title.value === suggestedTitle)
  {
    suggestedTitle = bookmark.title
    title.value = suggestedTitle
  }
  syncInputs()
}
function updateRange(): void
{
  const start = parseTimecode(startInput.value)
  const end = parseTimecode(endInput.value)
  if (start !== undefined && end !== undefined && start <= end && end <= props.durationMs)
  {
    range.value = [start, end]
    syncInputs()
  }
}
watch(opened, value =>
{
  if (value)
  {
    range.value = resolveClipRange(props.selection, props.currentMs, props.durationMs)
    startBookmarkId.value = undefined
    endBookmarkId.value = undefined
    suggestedTitle = props.sessionTitle
    title.value = props.sessionTitle
    mode.value = 'Exact'
    error.value = ''
    syncInputs()
  }
}, { immediate: true })
async function submit(): Promise<void>
{
  const startMs = parseTimecode(startInput.value)
  const endMs = parseTimecode(endInput.value)
  if (startMs === undefined || endMs === undefined || startMs < 0 || endMs <= startMs || endMs > props.durationMs)
  {
    error.value = t('components.player.invalidClipRange')
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

<style scoped>
.clip-bookmark-inputs { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 16px; }
@media (max-width: 480px) { .clip-bookmark-inputs { grid-template-columns: minmax(0, 1fr); } }
</style>
