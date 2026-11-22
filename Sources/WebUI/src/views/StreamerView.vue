<template>
  <PageHeader :title="streamer?.name || t('views.common.streamerRecordings')"
              :eyebrow="streamer ? t('views.streamer.room', { roomId: streamer.roomId }) : undefined">
    <template #avatar>
      <v-avatar v-if="streamer" size="66" color="surface-variant">
        <v-img :src="MediaAPI.avatar(streamer.id)"/>
      </v-avatar>
    </template>
    <StatusChip v-if="streamer?.isLive" status="live"/>
    <v-btn variant="outlined" prepend-icon="mdi-arrow-left" to="/">{{ t('views.common.library') }}</v-btn>
  </PageHeader>
  <v-alert v-if="sessionError || error" type="error" class="mb-6">{{ sessionError || error }}</v-alert>
  <SessionLibrary :sessions="sessions" @removed="removeSessions" @retention-changed="updateSessionRetention">
    <template #filters>
      <v-text-field v-model="search" class="session-search" :label="t('views.streamer.search')"
                    prepend-inner-icon="mdi-magnify" clearable hide-details density="compact"
                    @keydown.enter="applySearch"/>
    </template>
  </SessionLibrary>
  <v-progress-linear v-if="loading && !sessions.length" indeterminate color="primary"/>
  <LoadMoreButton :has-more="hasMore" :loading="loadingMore" @load="loadMore"/>
  <EmptyState v-if="!sessions.length && !loading"
              :title="searchQuery ? t('views.common.noMatchingRecordings') : t('views.common.noRecordings')"/>
</template>

<script setup lang="ts">
import {useI18n} from 'vue-i18n'
import {onBeforeUnmount, ref, watch} from 'vue'
import {useRoute} from 'vue-router'
import {MediaAPI} from '@/apis/MediaAPI'
import {useAction} from '@/composables/useAction'
import {usePolling} from '@/composables/usePolling'
import {useSessionPages} from '@/composables/useSessionPages'
import type {Streamer} from '@/models/media'
import PageHeader from '@/components/common/PageHeader.vue'
import EmptyState from '@/components/common/EmptyState.vue'
import LoadMoreButton from '@/components/common/LoadMoreButton.vue'
import StatusChip from '@/components/common/StatusChip.vue'
import SessionLibrary from '@/components/library/SessionLibrary.vue'

const {t} = useI18n()

const route = useRoute()
const streamer = ref<Streamer>()
const search = ref<string | null>('')
const searchQuery = ref('')
const {error, run} = useAction()
const {
  sessions,
  loading,
  loadingMore,
  hasMore,
  error: sessionError,
  loadMore,
  removeSessions,
  updateSessionRetention
} = useSessionPages(() => ({streamerId: String(route.params.id), search: searchQuery.value}))
let searchTimer: ReturnType<typeof setTimeout> | undefined

function applySearch(): void {
  clearTimeout(searchTimer)
  searchQuery.value = (search.value ?? '').trim()
}

watch(search, () => {
  clearTimeout(searchTimer)
  searchTimer = setTimeout(applySearch, 350)
})
usePolling(async () => {
  await run(async () => {
    streamer.value = (await MediaAPI.streamers()).find(item => item.id === route.params.id)
  })
}, 15000)
onBeforeUnmount(() => {
  clearTimeout(searchTimer)
})
</script>

<style scoped>
.session-search {
  flex: 1 1 180px;
  min-width: 160px;
  max-width: 360px;
}
</style>
