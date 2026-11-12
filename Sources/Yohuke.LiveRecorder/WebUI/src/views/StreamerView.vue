<template>
  <PageHeader :title="streamer?.name || '主播录播'" :eyebrow="streamer ? `直播间 ${streamer.roomId}` : undefined">
    <template #avatar><v-avatar v-if="streamer" size="66" color="surface-variant"><v-img :src="MediaAPI.avatar(streamer.id)" /></v-avatar></template>
    <StatusChip v-if="streamer?.isLive" status="live" />
    <v-btn variant="outlined" prepend-icon="mdi-arrow-left" to="/">媒体库</v-btn>
  </PageHeader>
  <v-alert v-if="sessionError || error" type="error" class="mb-6">{{ sessionError || error }}</v-alert>
  <div class="filter-bar"><v-text-field v-model="search" label="搜索标题" prepend-inner-icon="mdi-magnify" clearable density="compact" style="max-width: 360px" @keydown.enter="applySearch" /></div>
  <v-progress-linear v-if="loading && !sessions.length" indeterminate color="primary" />
  <div v-if="sessions.length" class="media-grid"><SessionCard v-for="session in sessions" :key="session.id" :session="session" /></div>
  <LoadMoreButton :has-more="hasMore" :loading="loadingMore" @load="loadMore" />
  <EmptyState v-if="!sessions.length && !loading" :title="searchQuery ? '没有匹配的录播' : '暂无录播'" />
</template>

<script setup lang="ts">
import { onBeforeUnmount, ref, watch } from 'vue'
import { useRoute } from 'vue-router'
import { MediaAPI } from '@/apis/MediaAPI'
import { useAction } from '@/composables/useAction'
import { usePolling } from '@/composables/usePolling'
import { useSessionPages } from '@/composables/useSessionPages'
import type { Streamer } from '@/models/media'
import PageHeader from '@/components/common/PageHeader.vue'
import EmptyState from '@/components/common/EmptyState.vue'
import LoadMoreButton from '@/components/common/LoadMoreButton.vue'
import StatusChip from '@/components/common/StatusChip.vue'
import SessionCard from '@/components/library/SessionCard.vue'

const route = useRoute()
const streamer = ref<Streamer>()
const search = ref<string | null>('')
const searchQuery = ref('')
const { error, run } = useAction()
const { sessions, loading, loadingMore, hasMore, error: sessionError, loadMore } = useSessionPages(() => ({ streamerId: String(route.params.id), search: searchQuery.value }))
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
usePolling(async () =>
{
  await run(async () => { streamer.value = (await MediaAPI.streamers()).find(item => item.id === route.params.id) })
}, 15000)
onBeforeUnmount(() => { clearTimeout(searchTimer) })
</script>
