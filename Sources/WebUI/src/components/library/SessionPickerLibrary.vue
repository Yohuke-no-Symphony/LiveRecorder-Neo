<template>
  <div class="session-picker-library">
    <div class="session-picker-search">
      <v-text-field v-model="search" :label="t('views.library.search')" prepend-inner-icon="mdi-magnify" clearable hide-details density="compact" @keydown.enter="applySearch" />
    </div>
    <v-alert v-if="sessionError || streamerError" type="error" variant="tonal" density="compact" class="mb-4"><div class="picker-error"><span>{{ sessionError || streamerError }}</span><v-btn variant="text" size="small" :disabled="loading || streamerLoading" @click="retry">{{ t('views.common.retry') }}</v-btn></div></v-alert>
    <v-progress-linear v-if="loading || streamerLoading && !streamers.length" indeterminate color="primary" class="mb-4" />
    <div v-if="streamerId" class="picker-context mb-4">
      <v-btn variant="text" size="small" prepend-icon="mdi-arrow-left" @click="chooseStreamer()">{{ t('components.library.allStreamers') }}</v-btn>
      <h2 class="section-heading">{{ selectedStreamer?.name || t('views.common.streamerRecordings') }}</h2>
    </div>
    <template v-else-if="visibleStreamers.length">
      <div class="picker-section-heading mb-3"><h2 class="section-heading">{{ t('views.library.streamers') }}</h2><span class="text-caption muted">{{ formatNumber(visibleStreamers.length) }}</span></div>
      <div class="streamer-grid mb-6"><StreamerCard v-for="streamer in visibleStreamers" :key="streamer.id" :streamer="streamer" pick-mode @pick="chooseStreamer(streamer.id)" /></div>
    </template>
    <h2 class="section-heading">{{ searchQuery ? t('views.library.results') : streamerId ? t('views.common.streamerRecordings') : t('views.library.recent') }}</h2>
    <div class="media-grid"><SessionCard v-for="session in sessions" :key="session.id" :session="session" :streamer-name="streamerNames[session.streamerId]" :selected="session.id === selectedId" pick-mode @pick="emit('pick', session.id)" /></div>
    <LoadMoreButton :has-more="hasMore" :loading="loadingMore" @load="loadMore" />
    <EmptyState v-if="!sessions.length && !loading && !sessionError" :title="searchQuery ? t('views.common.noMatchingRecordings') : t('views.common.noRecordings')" />
  </div>
</template>

<script setup lang="ts">
import { computed, onScopeDispose, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { MediaAPI } from '@/apis/MediaAPI'
import { useSessionPages } from '@/composables/useSessionPages'
import { usePolling } from '@/composables/usePolling'
import type { Streamer } from '@/models/media'
import { formatNumber } from '@/utilities/format'
import EmptyState from '@/components/common/EmptyState.vue'
import LoadMoreButton from '@/components/common/LoadMoreButton.vue'
import StreamerCard from './StreamerCard.vue'
import SessionCard from './SessionCard.vue'

defineProps<{ selectedId: string }>()
const emit = defineEmits<{ pick: [id: string] }>()
const { t } = useI18n()
const search = ref<string | null>('')
const searchQuery = ref('')
const streamerId = ref<string>()
const streamers = ref<Streamer[]>([])
const streamerLoading = ref(false)
const streamerError = ref('')
const { sessions, loading, loadingMore, hasMore, error: sessionError, reload, loadMore } = useSessionPages(() => ({ streamerId: streamerId.value, search: searchQuery.value }))
const streamerNames = computed(() => Object.fromEntries(streamers.value.map(streamer => [streamer.id, streamer.name])))
const selectedStreamer = computed(() => streamers.value.find(streamer => streamer.id === streamerId.value))
const visibleStreamers = computed(() => streamers.value.filter(streamer => !searchQuery.value || streamer.name.toLowerCase().includes(searchQuery.value.toLowerCase()) || String(streamer.roomId).includes(searchQuery.value)))
let searchTimer: ReturnType<typeof setTimeout> | undefined
let disposed = false

function applySearch(): void
{
  clearTimeout(searchTimer)
  searchQuery.value = (search.value ?? '').trim()
}

function chooseStreamer(id?: string): void
{
  streamerId.value = id
  search.value = ''
  applySearch()
}

async function loadStreamers(): Promise<void>
{
  streamerLoading.value = true
  try
  {
    const result = await MediaAPI.streamers()
    if (!disposed)
    {
      streamers.value = result
      streamerError.value = ''
    }
  }
  catch (value)
  {
    if (!disposed)
    {
      streamerError.value = value instanceof Error ? value.message : t('components.library.loadFailed')
    }
  }
  finally
  {
    if (!disposed)
    {
      streamerLoading.value = false
    }
  }
}

function retry(): void
{
  void reload()
  void loadStreamers()
}

watch(search, () =>
{
  clearTimeout(searchTimer)
  searchTimer = setTimeout(applySearch, 350)
})
usePolling(loadStreamers, 15000)
onScopeDispose(() => { disposed = true; clearTimeout(searchTimer) })
</script>

<style scoped>
.session-picker-search { position: sticky; top: 0; z-index: 2; padding: 8px 0 16px; background: rgb(var(--v-theme-surface)); }
.picker-section-heading, .picker-context, .picker-error { display: flex; align-items: center; flex-wrap: wrap; gap: 10px; }
.picker-section-heading, .picker-error { justify-content: space-between; }
.picker-context .section-heading, .picker-section-heading .section-heading { margin: 0; min-width: 0; overflow-wrap: anywhere; }
.picker-context > .v-btn { flex-shrink: 0; }
.session-picker-library .media-grid { grid-template-columns: repeat(auto-fill, minmax(min(240px, 100%), 1fr)); }
</style>
