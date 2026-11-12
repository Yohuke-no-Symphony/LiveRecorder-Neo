<template>
  <PageHeader title="录播库">
    <v-text-field v-model="search" label="搜索录播" prepend-inner-icon="mdi-magnify" clearable width="300" density="compact" @keydown.enter="applySearch" />
  </PageHeader>
  <v-alert v-if="sessionError || error" type="error" class="mb-6">{{ sessionError || error }}</v-alert>
  <v-progress-linear v-if="loading && !sessions.length" indeterminate color="primary" />
  <template v-if="visibleStreamers.length">
    <div class="d-flex align-center justify-space-between mb-4"><h2 class="section-heading mb-0">主播</h2><span class="text-caption muted">{{ visibleStreamers.length }}</span></div>
    <div class="streamer-grid mb-10"><StreamerCard v-for="streamer in visibleStreamers" :key="streamer.id" :streamer="streamer" /></div>
  </template>
  <h2 class="section-heading">{{ searchQuery ? '搜索结果' : '最近录播' }}</h2>
  <div v-if="sessions.length" class="media-grid"><SessionCard v-for="session in sessions" :key="session.id" :session="session" :streamer-name="streamerNames[session.streamerId]" /></div>
  <LoadMoreButton :has-more="hasMore" :loading="loadingMore" @load="loadMore" />
  <EmptyState v-if="!sessions.length && !loading" :title="searchQuery ? '没有匹配的录播' : '暂无录播'">
    <v-btn v-if="auth.isAdmin && !searchQuery" color="primary" to="/admin/targets" prepend-icon="mdi-plus">添加录制</v-btn>
  </EmptyState>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { MediaAPI } from '@/apis/MediaAPI'
import { useAuthStore } from '@/stores/auth'
import { useAction } from '@/composables/useAction'
import { usePolling } from '@/composables/usePolling'
import { useSessionPages } from '@/composables/useSessionPages'
import type { Streamer } from '@/models/media'
import PageHeader from '@/components/common/PageHeader.vue'
import EmptyState from '@/components/common/EmptyState.vue'
import LoadMoreButton from '@/components/common/LoadMoreButton.vue'
import SessionCard from '@/components/library/SessionCard.vue'
import StreamerCard from '@/components/library/StreamerCard.vue'

const auth = useAuthStore()
const search = ref<string | null>('')
const searchQuery = ref('')
const streamers = ref<Streamer[]>([])
const { error, run } = useAction()
const { sessions, loading, loadingMore, hasMore, error: sessionError, loadMore } = useSessionPages(() => ({ search: searchQuery.value }))
const streamerNames = computed(() => Object.fromEntries(streamers.value.map(streamer => [streamer.id, streamer.name])))
const visibleStreamers = computed(() => streamers.value.filter(streamer => !searchQuery.value || streamer.name.toLowerCase().includes(searchQuery.value.toLowerCase()) || String(streamer.roomId).includes(searchQuery.value)))
let searchTimer: ReturnType<typeof setTimeout> | undefined

function applySearch(): void
{
  clearTimeout(searchTimer)
  searchQuery.value = (search.value ?? '').trim()
}
watch(search, () =>
{
  clearTimeout(searchTimer)
  searchTimer = setTimeout(applySearch, 350)
})
usePolling(async () => { await run(async () => { streamers.value = await MediaAPI.streamers() }) }, 15000)
onBeforeUnmount(() => { clearTimeout(searchTimer) })
</script>
