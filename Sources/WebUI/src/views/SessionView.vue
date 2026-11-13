<template>
  <v-alert v-if="error" type="error" class="mb-6">{{ error }}</v-alert>
  <v-progress-linear v-if="!detail && busy" indeterminate color="primary" />
  <template v-if="detail">
    <div class="session-heading mb-6"><router-link :to="`/streamers/${detail.session.streamerId}`" class="text-body-2 muted"><v-icon icon="mdi-chevron-left" size="18" />{{ streamer?.name || t('views.common.streamerRecordings') }}</router-link><StatusChip :status="detail.session.state" /></div>
    <div class="playback-layout">
      <div class="playback-main">
        <MediaPlayer ref="player" v-model="offsetMs" :duration-ms="detail.durationMs" :gaps="detail.gaps" :is-live="isLive" :initial-offset-ms="initialOffset" :load-window="loadWindow" :timeline="timeline" :bookmarks="bookmarks" :selection="clipSelection" :timeline-loading="timelineLoading" :timeline-error="timelineError" @refresh-timeline="refreshTimeline" @navigate="feedNavigationRevision++">
          <template #actions><PlaybackBookmarkActions :current-ms="offsetMs" :duration-ms="detail.durationMs" :selection="clipSelection" :invalid="clipSelectionInvalid" :saving="bookmarkSaving" :editable="bookmarksEditable" @bookmark="openBookmark" @start="setClipStart" @end="setClipEnd" @clear="clipSelection = {}" /></template>
        </MediaPlayer>
        <div class="session-meta">
          <h1>{{ detail.session.title }}</h1>
          <div class="d-flex align-center flex-wrap ga-3 mt-3"><span class="text-caption muted">{{ formatDate(detail.session.startedAtUtc) }}</span><span class="text-caption mono muted">{{ formatDuration(detail.durationMs, true) }}</span><span class="text-caption muted">{{ formatBytes(detail.session.totalBytes) }}</span><v-spacer /><v-btn color="primary" prepend-icon="mdi-content-cut" :disabled="detail.durationMs <= 0 || clipSelectionInvalid" @click="clipOpen = true">{{ t('views.session.createClip') }}</v-btn><v-menu v-if="auth.isAdmin && isFinished"><template #activator="{ props: menuProps }"><v-btn v-bind="menuProps" icon="mdi-dots-horizontal" variant="text" :aria-label="t('views.session.actions')" /></template><v-list density="compact"><v-list-item :title="t('views.session.group')" prepend-icon="mdi-folder-multiple-outline" @click="groupOpen = true" /><v-list-item :title="t('views.session.split')" prepend-icon="mdi-call-split" :disabled="detail.segments.length < 2" @click="splitOpen = true" /><v-divider /><v-list-item :title="t('views.session.repair')" prepend-icon="mdi-wrench-outline" @click="openMaintenance('Repair')" /><v-list-item :title="t('views.session.merge')" prepend-icon="mdi-call-merge" @click="openMaintenance('Merge')" /></v-list></v-menu></div>
        </div>
        <SessionBookmarkList class="mt-6" :bookmarks="bookmarks" :loading="bookmarksLoading" :saving="bookmarkSaving" :error="bookmarksError" :editable="bookmarksEditable" @seek="player?.seek($event)" @start="setClipStart" @end="setClipEnd" @edit="editBookmark" @delete="confirmBookmarkDelete" @refresh="refreshBookmarks" />
        <v-expansion-panels v-if="detail.segments.length" variant="accordion" class="mt-6"><v-expansion-panel :title="t('views.session.segments', { count: formatNumber(detail.segments.length) })"><v-expansion-panel-text><v-table density="comfortable"><thead><tr><th>{{ t('views.session.segment') }}</th><th>{{ t('views.session.start') }}</th><th>{{ t('views.session.duration') }}</th><th>{{ t('views.common.size') }}</th><th>{{ t('views.common.status') }}</th></tr></thead><tbody><tr v-for="(segment, index) in detail.segments" :key="segment.id" class="clickable-row" @click="player?.seek(segment.startOffsetMs)"><td>{{ String(index + 1).padStart(2, '0') }}</td><td class="mono text-caption">{{ formatDuration(segment.startOffsetMs, true) }}</td><td class="mono text-caption">{{ formatDuration(segment.durationMs) }}</td><td class="mono text-caption">{{ formatBytes(segment.committedBytes) }}</td><td><StatusChip :status="segment.isClosed ? 'completed' : 'recording'" /></td></tr></tbody></v-table></v-expansion-panel-text></v-expansion-panel></v-expansion-panels>
      </div>
      <EventFeed class="playback-feed" :navigation-revision="feedNavigationRevision" :session-id="id" :offset-ms="offsetMs" :enabled="detail.session.hasDanmu" :is-live="isLive" @seek="player?.seek($event)" />
    </div>
    <ClipDialog v-model="clipOpen" :session-id="id" :session-title="detail.session.title" :current-ms="offsetMs" :duration-ms="detail.durationMs" :selection="clipSelection" :bookmarks="bookmarks" @created="onClipCreated" />
    <SessionBookmarkDialog v-model="bookmarkOpen" :bookmark="editingBookmark" :offset-ms="bookmarkOffsetMs" :duration-ms="detail.durationMs" :busy="bookmarkSaving" :error="bookmarksError" @save="saveBookmark" />
    <ConfirmDialog :model-value="!!deletingBookmark" :title="t('components.bookmarks.delete')" :message="t('components.bookmarks.deleteConfirm', { title: deletingBookmark?.title ?? '' })" :action-label="t('components.bookmarks.delete')" :busy="bookmarkSaving" :error="bookmarksError" danger @update:model-value="!$event && (deletingBookmark = undefined)" @confirm="deleteBookmark" />
    <SessionGroupDialog v-if="groupOpen && auth.isAdmin && isFinished" v-model="groupOpen" :session="detail.session" @grouped="router.replace(`/sessions/${$event.id}`)" />
    <SessionSplitDialog v-if="splitOpen && auth.isAdmin && isFinished" v-model="splitOpen" :session-id="id" :segments="detail.segments" @split="router.replace(`/streamers/${detail.session.streamerId}`)" />
    <v-dialog v-model="maintenanceOpen" max-width="580"><v-card class="pa-6"><h2 class="text-h6 mb-5">{{ maintenanceKind === 'Repair' ? t('views.session.repair') : t('views.session.merge') }}</h2><div class="maintenance-segments"><v-checkbox v-for="(segment, index) in detail.segments.filter(item => item.isClosed)" :key="segment.id" v-model="selectedSegments" :value="segment.id" :label="t('views.session.segmentDescription', { number: String(index + 1).padStart(2, '0'), start: formatDuration(segment.startOffsetMs, true), size: formatBytes(segment.committedBytes) })" /></div><v-alert v-if="maintenanceError" type="error" class="mt-4">{{ maintenanceError }}</v-alert><div class="d-flex justify-end ga-3 mt-6"><v-btn variant="text" @click="maintenanceOpen = false">{{ t('views.common.cancel') }}</v-btn><v-btn color="primary" :loading="maintenanceBusy" :disabled="!selectedSegments.length" @click="createMaintenance">{{ t('views.session.createJob') }}</v-btn></div></v-card></v-dialog>
    <v-snackbar v-model="jobCreated" color="surface-variant">{{ t('views.session.jobCreated') }}<template #actions><v-btn color="primary" to="/jobs">{{ t('views.session.view') }}</v-btn></template></v-snackbar>
  </template>
</template>

<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { computed, onMounted, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { MediaAPI } from '@/apis/MediaAPI'
import { useAuthStore } from '@/stores/auth'
import { useAction } from '@/composables/useAction'
import { usePolling } from '@/composables/usePolling'
import { useSessionTimeline } from '@/composables/useSessionTimeline'
import type { SessionDetail, Streamer } from '@/models/media'
import type { SessionBookmark } from '@/models/sessionBookmark'
import type { ClipSelection } from '@/models/clipSelection'
import { useSessionBookmarks } from '@/composables/useSessionBookmarks'
import { clampOffset } from '@/utilities/timeline'
import { resolveClipRange } from '@/utilities/clipSelection'
import { formatBytes, formatDate, formatDuration, formatNumber } from '@/utilities/format'
import MediaPlayer from '@/components/player/MediaPlayer.vue'
import EventFeed from '@/components/player/EventFeed.vue'
import ClipDialog from '@/components/player/ClipDialog.vue'
import PlaybackBookmarkActions from '@/components/player/PlaybackBookmarkActions.vue'
import SessionBookmarkList from '@/components/player/SessionBookmarkList.vue'
import SessionBookmarkDialog from '@/components/player/SessionBookmarkDialog.vue'
import ConfirmDialog from '@/components/common/ConfirmDialog.vue'
import StatusChip from '@/components/common/StatusChip.vue'
import SessionGroupDialog from '@/components/library/SessionGroupDialog.vue'
import SessionSplitDialog from '@/components/library/SessionSplitDialog.vue'

const { t } = useI18n()

const route = useRoute()
const router = useRouter()
const auth = useAuthStore()
const id = String(route.params.id)
const detail = ref<SessionDetail>()
const streamers = ref<Streamer[]>([])
const streamer = computed(() => streamers.value.find(item => item.id === detail.value?.session.streamerId))
const offsetMs = ref(0)
const feedNavigationRevision = ref(0)
const initialOffset = Math.max(0, Number(route.query.at) || 0)
const player = ref<InstanceType<typeof MediaPlayer>>()
const clipOpen = ref(false)
const clipSelection = ref<ClipSelection>({})
const clipSelectionInvalid = computed(() =>
{
  const [startMs, endMs] = resolveClipRange(clipSelection.value, offsetMs.value, detail.value?.durationMs ?? 0)
  return (clipSelection.value.startMs !== undefined || clipSelection.value.endMs !== undefined) && endMs <= startMs
})
const bookmarkOpen = ref(false)
const bookmarkOffsetMs = ref(0)
const editingBookmark = ref<SessionBookmark>()
const deletingBookmark = ref<SessionBookmark>()
const bookmarksEditable = computed(() => detail.value?.session.isHidden !== true)
const { bookmarks, loading: bookmarksLoading, saving: bookmarkSaving, error: bookmarksError, refresh: refreshBookmarks, create: createBookmark, update: updateBookmark, remove: removeBookmark, clearError: clearBookmarksError } = useSessionBookmarks(() => id, () => !!detail.value)
const groupOpen = ref(false)
const splitOpen = ref(false)
const jobCreated = ref(false)
const maintenanceOpen = ref(false)
const maintenanceKind = ref<'Repair' | 'Merge'>('Repair')
const selectedSegments = ref<string[]>([])
const { busy, error, run } = useAction()
const { busy: maintenanceBusy, error: maintenanceError, run: runMaintenance } = useAction()
const isLive = computed(() => detail.value?.session.state === 'Recording')
const { timeline, loading: timelineLoading, error: timelineError, refresh: refreshTimeline } = useSessionTimeline(() => id, () => !!detail.value?.session.hasDanmu, isLive)
const isFinished = computed(() => !!detail.value?.session.endedAtUtc && !isLive.value)
const loadWindow = (value: number) => MediaAPI.playback(id, value)
usePolling(async () => { await run(async () => { detail.value = await MediaAPI.session(id) }) }, 10000)
onMounted(async () =>
{
  await run(async () =>
  {
    streamers.value = await MediaAPI.streamers()
  })
})
function setClipStart(value: number): void
{
  clipSelection.value = { ...clipSelection.value, startMs: Math.floor(clampOffset(value, detail.value?.durationMs ?? 0)) }
}
function setClipEnd(value: number): void
{
  clipSelection.value = { ...clipSelection.value, endMs: Math.floor(clampOffset(value, detail.value?.durationMs ?? 0)) }
}
function onClipCreated(): void
{
  jobCreated.value = true
  clipSelection.value = {}
}
async function openBookmark(value: number): Promise<void>
{
  clearBookmarksError()
  editingBookmark.value = undefined
  bookmarkOffsetMs.value = Math.floor(clampOffset(value, detail.value?.durationMs ?? 0))
  if (document.fullscreenElement) { await document.exitFullscreen() }
  bookmarkOpen.value = true
}
function editBookmark(bookmark: SessionBookmark): void
{
  clearBookmarksError()
  editingBookmark.value = { ...bookmark }
  bookmarkOffsetMs.value = bookmark.offsetMs
  bookmarkOpen.value = true
}
async function saveBookmark(value: { title: string; offsetMs: number }): Promise<void>
{
  const bookmark = editingBookmark.value
  const saved = bookmark ? await updateBookmark(bookmark.id, value.title, value.offsetMs) : await createBookmark(value.title, value.offsetMs)
  if (saved) { bookmarkOpen.value = false }
}
function confirmBookmarkDelete(bookmark: SessionBookmark): void
{
  clearBookmarksError()
  deletingBookmark.value = bookmark
}
async function deleteBookmark(): Promise<void>
{
  const bookmark = deletingBookmark.value
  if (bookmark && await removeBookmark(bookmark.id)) { deletingBookmark.value = undefined }
}
function openMaintenance(kind: 'Repair' | 'Merge'): void
{
  maintenanceKind.value = kind
  selectedSegments.value = detail.value?.segments.filter(item => item.isClosed).map(item => item.id) ?? []
  maintenanceError.value = ''
  maintenanceOpen.value = true
}
async function createMaintenance(): Promise<void>
{
  await runMaintenance(async () =>
  {
    await MediaAPI.createJob({ kind: maintenanceKind.value, sessionId: id, mode: 'Exact', startMs: 0, endMs: detail.value?.durationMs ?? 0, segmentIds: selectedSegments.value })
    maintenanceOpen.value = false
    jobCreated.value = true
  })
}
</script>

<style scoped>
.session-heading { display: flex; justify-content: space-between; align-items: center; }
.playback-layout { display: grid; grid-template-columns: minmax(0, 1fr) 360px; gap: 24px; align-items: start; }
.playback-feed { position: sticky; top: calc(var(--v-layout-top, 0px) + 24px); height: calc(100dvh - var(--v-layout-top, 0px) - 120px); }
.playback-main { min-width: 0; }.session-meta { padding: 24px 1px 0; }.session-meta h1 { font-size: 1.55rem; font-weight: 600; line-height: 1.5; letter-spacing: -.025em; }
.maintenance-segments { max-height: 380px; overflow-y: auto; }
@media (min-width: 1600px) { .playback-layout { grid-template-columns: minmax(0, 1fr) 390px; } }
@media (max-width: 1200px) { .playback-layout { grid-template-columns: minmax(0, 1fr); } .playback-feed { position: static; height: calc(100dvh - var(--v-layout-top, 0px) - 48px); } }
</style>
