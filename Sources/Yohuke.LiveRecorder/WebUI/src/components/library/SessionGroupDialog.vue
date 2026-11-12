<template>
  <v-dialog v-model="opened" :persistent="busy" max-width="720" scrollable>
    <v-card>
      <div class="d-flex justify-space-between align-center ga-4 pa-6"><h2 class="text-h6">归组场次</h2><v-chip v-if="selectedIds.length" color="primary">已选 {{ selectedIds.length }}</v-chip></div>
      <v-card-text class="pt-0">
        <div class="form-stack mb-5"><v-text-field v-model="title" label="归组标题" :disabled="busy" maxlength="180" /><v-text-field v-model="search" label="搜索场次" prepend-inner-icon="mdi-magnify" clearable :disabled="busy" @keydown.enter="applySearch" /></div>
        <v-alert v-if="error || pageError" type="error" class="mb-4">{{ error || pageError }}</v-alert>
        <v-progress-linear v-if="loading" indeterminate color="primary" />
        <div v-for="session in candidates" :key="session.id" class="group-candidate"><v-checkbox v-model="selectedIds" :value="session.id" :disabled="busy" :aria-label="`选择 ${session.title}`" density="compact" /><div class="flex-grow-1" @click="toggle(session.id)"><div class="text-body-2">{{ session.title }}</div><div class="text-caption muted mt-1">{{ formatDate(session.startedAtUtc) }} · {{ formatDuration(duration(session)) }}</div></div></div>
        <LoadMoreButton v-if="!busy" :has-more="hasMore" :loading="loadingMore" @load="loadMore" />
        <EmptyState v-if="!loading && !candidates.length" title="暂无可选场次" />
      </v-card-text>
      <div class="d-flex justify-end ga-3 pa-6"><v-btn variant="text" :disabled="busy" @click="opened = false">取消</v-btn><v-btn color="primary" :loading="busy" :disabled="busy || !selectedIds.length" @click="submit">归组</v-btn></div>
    </v-card>
  </v-dialog>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { MediaAPI } from '@/apis/MediaAPI'
import { useAction } from '@/composables/useAction'
import { useSessionPages } from '@/composables/useSessionPages'
import type { BroadcastSession } from '@/models/media'
import { formatDate, formatDuration } from '@/utilities/format'
import EmptyState from '@/components/common/EmptyState.vue'
import LoadMoreButton from '@/components/common/LoadMoreButton.vue'

const opened = defineModel<boolean>({ required: true })
const props = defineProps<{ session: BroadcastSession }>()
const emit = defineEmits<{ grouped: [session: BroadcastSession] }>()
const title = ref(props.session.title)
const search = ref<string | null>('')
const searchQuery = ref('')
const selectedIds = ref<string[]>([])
const { busy, error, run } = useAction()
const { sessions, loading, loadingMore, hasMore, error: pageError, loadMore } = useSessionPages(() => ({ streamerId: props.session.streamerId, search: searchQuery.value }))
const candidates = computed(() => sessions.value.filter(session => session.id !== props.session.id && session.endedAtUtc && session.state !== 'Recording'))
const duration = (session: BroadcastSession) => Math.max(0, new Date(session.endedAtUtc!).getTime() - new Date(session.startedAtUtc).getTime())
let searchTimer: ReturnType<typeof setTimeout> | undefined
function applySearch(): void
{
  clearTimeout(searchTimer)
  searchQuery.value = (search.value ?? '').trim()
}
watch(search, () => { clearTimeout(searchTimer); searchTimer = setTimeout(applySearch, 350) })
function toggle(id: string): void
{
  if (busy.value) { return }
  selectedIds.value = selectedIds.value.includes(id) ? selectedIds.value.filter(value => value !== id) : [...selectedIds.value, id]
}
async function submit(): Promise<void>
{
  if (busy.value || !selectedIds.value.length) { return }
  await run(async () =>
  {
    const result = await MediaAPI.groupSessions(props.session.id, [...selectedIds.value], title.value.trim() || undefined)
    opened.value = false
    emit('grouped', result)
  })
}
onBeforeUnmount(() => { clearTimeout(searchTimer) })
</script>

<style scoped>.group-candidate { display: flex; align-items: center; gap: 8px; padding: 12px 0; border-bottom: 1px solid rgba(var(--v-theme-on-surface), .06); }.group-candidate > div:last-child { cursor: pointer; }</style>
