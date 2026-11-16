<template>
  <v-alert v-if="error || (retentionError && !cancelPermanentOpen)" type="error" class="mb-6">{{ error || retentionError }}<template v-if="error" #append><v-btn variant="text" size="small" :disabled="busy" @click="refreshSession">{{ t('views.common.retry') }}</v-btn></template></v-alert>
  <v-progress-linear v-if="!detail && busy" indeterminate color="primary" />
  <v-defaults-provider v-if="detail" :defaults="playbackOverlayDefaults">
    <div class="session-heading mb-6"><router-link :to="`/streamers/${detail.session.streamerId}`" class="text-body-2 muted"><v-icon icon="mdi-chevron-left" size="18" />{{ streamer?.name || t('views.common.streamerRecordings') }}</router-link><StatusChip :status="detail.session.state" /></div>
    <div ref="playbackLayout" class="playback-layout">
      <div class="playback-main">
        <MediaPlayer ref="player" v-model="offsetMs" class="playback-player" :fullscreen-target="playbackLayout" :duration-ms="detail.durationMs" :gaps="detail.gaps" :is-live="isLive" :initial-offset-ms="initialOffset" :load-window="loadWindow" :timeline="timeline" :bookmarks="bookmarks" :selection="clipSelection" :timeline-loading="timelineLoading" :timeline-error="timelineError" @refresh-timeline="refreshTimeline" @navigate="feedNavigationRevision++">
          <template #actions><PlaybackBookmarkActions :current-ms="offsetMs" :duration-ms="detail.durationMs" :selection="clipSelection" :invalid="clipSelectionInvalid" :saving="bookmarkSaving" :editable="bookmarksEditable" @bookmark="openBookmark" @start="setClipStart" @end="setClipEnd" @clear="clipSelection = {}" /></template>
        </MediaPlayer>
        <div class="session-meta">
          <h1>{{ detail.session.title }}</h1>
          <div class="session-details">
            <div class="session-info">
              <v-tooltip :text="t('views.session.startedAt')" location="top">
                <template #activator="{ props: tooltipProps }">
                  <span v-bind="tooltipProps" class="session-info-item" tabindex="0"><v-icon icon="mdi-calendar-clock-outline" size="16" />{{ formatDate(detail.session.startedAtUtc) }}</span>
                </template>
              </v-tooltip>
              <v-tooltip :text="t('views.session.duration')" location="top">
                <template #activator="{ props: tooltipProps }">
                  <span v-bind="tooltipProps" class="session-info-item" tabindex="0"><v-icon icon="mdi-timer-outline" size="16" /><span class="mono">{{ formatDuration(detail.durationMs, true) }}</span></span>
                </template>
              </v-tooltip>
              <v-tooltip :text="t('views.common.size')" location="top">
                <template #activator="{ props: tooltipProps }">
                  <span v-bind="tooltipProps" class="session-info-item" tabindex="0"><v-icon icon="mdi-harddisk" size="16" />{{ formatBytes(detail.session.totalBytes) }}</span>
                </template>
              </v-tooltip>
              <v-tooltip v-if="detail.session.isPermanent" :text="t('shell.retention.noAutoPurge')" location="top">
                <template #activator="{ props: tooltipProps }">
                  <v-chip v-bind="tooltipProps" color="primary" size="small" prepend-icon="mdi-shield-check-outline" tabindex="0">{{ t('shell.retention.permanent') }}</v-chip>
                </template>
              </v-tooltip>
              <v-tooltip v-else-if="detail.session.expiresAtUtc" :text="t('shell.retention.expiryDescription')" location="top">
                <template #activator="{ props: tooltipProps }">
                  <SessionExpiry v-bind="tooltipProps" :session="detail.session" title="" tabindex="0" />
                </template>
              </v-tooltip>
            </div>
            <div class="session-actions">
              <v-tooltip v-if="auth.canViewAnalytics" :text="t('views.analytics.title')" location="top">
                <template #activator="{ props: tooltipProps }">
                  <v-btn v-bind="tooltipProps" icon="mdi-chart-timeline-variant" size="40" variant="tonal" :aria-label="t('views.analytics.title')" :to="{ path: '/analytics', query: { sessionId: detail.session.id } }" />
                </template>
              </v-tooltip>
              <v-tooltip v-if="auth.canConfigureRecordings" :text="t('shell.recordingConfiguration.title')" location="top">
                <template #activator="{ props: tooltipProps }">
                  <v-btn v-bind="tooltipProps" icon="mdi-cog-outline" size="40" variant="tonal" :aria-label="t('shell.recordingConfiguration.title')" @click="configurationOpen = true" />
                </template>
              </v-tooltip>
              <v-tooltip v-if="auth.canManageRecordings" :text="t(detail.session.isPermanent ? 'shell.retention.cancelPermanent' : 'shell.retention.keepPermanent')" location="top">
                <template #activator="{ props: tooltipProps }">
                  <v-btn v-bind="tooltipProps" icon="mdi-shield-check-outline" size="40" variant="tonal" :aria-label="t(detail.session.isPermanent ? 'shell.retention.cancelPermanent' : 'shell.retention.keepPermanent')" :loading="retentionBusy" @click="setPermanent" />
                </template>
              </v-tooltip>
              <v-tooltip v-if="auth.canDownloadRecordings" :text="t('components.download.open')" location="top">
                <template #activator="{ props: tooltipProps }">
                  <v-btn v-bind="tooltipProps" icon="mdi-download" size="40" variant="tonal" :aria-label="t('components.download.open')" @click="downloadOpen = true" />
                </template>
              </v-tooltip>
              <v-tooltip v-if="auth.canCreateClips" :text="t('views.session.createClip')" location="top">
                <template #activator="{ props: tooltipProps }">
                  <v-btn v-bind="tooltipProps" icon="mdi-content-cut" size="40" color="primary" :aria-label="t('views.session.createClip')" :disabled="detail.durationMs <= 0 || clipSelectionInvalid" @click="clipOpen = true" />
                </template>
              </v-tooltip>
              <v-menu v-if="auth.canManageRecordings && isFinished">
                <template #activator="{ props: menuProps }">
                  <v-tooltip :text="t('views.session.actions')" location="top">
                    <template #activator="{ props: tooltipProps }">
                      <v-btn v-bind="mergeProps(menuProps, tooltipProps)" icon="mdi-dots-horizontal" size="40" variant="tonal" :aria-label="t('views.session.actions')" />
                    </template>
                  </v-tooltip>
                </template>
                <v-list density="compact">
                  <v-list-item :title="t('views.session.group')" prepend-icon="mdi-folder-multiple-outline" @click="groupOpen = true" />
                  <v-list-item :title="t('views.session.split')" prepend-icon="mdi-call-split" :disabled="detail.segments.length < 2" @click="splitOpen = true" />
                  <v-divider v-if="auth.isAdmin" />
                  <v-list-item v-if="auth.isAdmin" :title="t('transcoding.transcode')" prepend-icon="mdi-video-box" @click="transcodeOpen = true" />
                  <v-list-item v-if="auth.isAdmin" :title="t('views.session.repair')" prepend-icon="mdi-wrench-outline" @click="openMaintenance('Repair')" />
                  <v-list-item v-if="auth.isAdmin" :title="t('views.session.merge')" prepend-icon="mdi-call-merge" @click="openMaintenance('Merge')" />
                </v-list>
              </v-menu>
            </div>
          </div>
        </div>
        <SessionBookmarkList class="mt-4" :bookmarks="bookmarks" :loading="bookmarksLoading" :saving="bookmarkSaving" :error="bookmarksError" :editable="bookmarksEditable" @seek="player?.seek($event)" @start="setClipStart" @end="setClipEnd" @edit="editBookmark" @delete="confirmBookmarkDelete" @refresh="refreshBookmarks" />
        <v-card v-if="detail.segments.length" class="session-segments mt-4" variant="flat">
          <v-expansion-panels variant="accordion" flat>
            <v-expansion-panel>
              <v-expansion-panel-title class="session-segments-title" static>{{ t('views.session.segments', { count: formatNumber(detail.segments.length) }) }}</v-expansion-panel-title>
              <v-expansion-panel-text>
                <v-table density="comfortable">
                  <thead><tr><th>{{ t('views.session.segment') }}</th><th>{{ t('views.session.start') }}</th><th>{{ t('views.session.duration') }}</th><th>{{ t('views.common.size') }}</th><th>{{ t('views.common.status') }}</th><th v-if="showSegmentActions" class="segment-action">{{ t('views.session.segmentActions') }}</th></tr></thead>
                  <tbody>
                    <tr v-for="(segment, index) in detail.segments" :key="segment.id" class="clickable-row" @click="player?.seek(segment.startOffsetMs)">
                      <td>{{ String(index + 1).padStart(2, '0') }}</td><td class="mono text-caption">{{ formatDuration(segment.startOffsetMs, true) }}</td><td class="mono text-caption">{{ formatDuration(segment.durationMs) }}</td><td class="mono text-caption">{{ formatBytes(segment.committedBytes) }}</td><td><StatusChip :status="segment.isClosed ? 'completed' : 'recording'" /></td>
                      <td v-if="showSegmentActions" class="segment-action" @click.stop>
                        <v-tooltip :text="segmentDeletionUnavailable || t('views.session.deleteSegmentNumber', { number: String(index + 1).padStart(2, '0') })" location="top">
                          <template #activator="{ props: tooltipProps }">
                            <span v-bind="tooltipProps" class="d-inline-flex" :tabindex="segmentDeletionUnavailable ? 0 : undefined">
                              <v-btn icon="mdi-delete-outline" size="small" variant="text" color="error" :aria-label="t('views.session.deleteSegmentNumber', { number: String(index + 1).padStart(2, '0') })" :disabled="!!segmentDeletionUnavailable || segmentDeleting" @click.stop="confirmSegmentDelete(segment, index)" />
                            </span>
                          </template>
                        </v-tooltip>
                      </td>
                    </tr>
                  </tbody>
                </v-table>
              </v-expansion-panel-text>
            </v-expansion-panel>
          </v-expansion-panels>
        </v-card>
      </div>
      <SessionPlaybackSidebar class="playback-feed" :navigation-revision="feedNavigationRevision" :session-id="id" :offset-ms="offsetMs" :duration-ms="detail.durationMs" :has-danmu="detail.session.hasDanmu" :is-live="isLive" :editable="bookmarksEditable" :configuration-revision="detail.session.configurationRevision" :ended="isFinished" :live-marker-file-id="requestedMarkerFileId" :show-live-markers="route.query.panel === 'markers'" @seek="player?.seek($event)" @configure="configurationOpen = true" />
    </div>
    <ClipDialog v-if="auth.canCreateClips" v-model="clipOpen" :session-id="id" :session-title="detail.session.title" :current-ms="offsetMs" :duration-ms="detail.durationMs" :selection="clipSelection" :bookmarks="bookmarks" @created="onClipCreated" />
    <TranscodeDialog v-if="auth.isAdmin" v-model="transcodeOpen" :session-ids="[id]" @created="jobCreated = true" />
    <SessionDownloadDialog v-if="auth.canDownloadRecordings" v-model="downloadOpen" :session-id="id" :session-title="detail.session.title" :segments="detail.segments" />
    <SessionBookmarkDialog v-model="bookmarkOpen" :bookmark="editingBookmark" :offset-ms="bookmarkOffsetMs" :duration-ms="detail.durationMs" :busy="bookmarkSaving" :error="bookmarksError" @save="saveBookmark" />
    <ConfirmDialog :model-value="!!deletingBookmark" :title="t('components.bookmarks.delete')" :message="t('components.bookmarks.deleteConfirm', { title: deletingBookmark?.title ?? '' })" :action-label="t('components.bookmarks.delete')" :busy="bookmarkSaving" :error="bookmarksError" danger @update:model-value="!$event && (deletingBookmark = undefined)" @confirm="deleteBookmark" />
    <ConfirmDialog v-if="auth.canManageRecordings" :model-value="!!deletingSegment" :title="t('views.session.deleteSegment')" :message="t('views.session.deleteSegmentConfirm', { number: deletingSegment?.number ?? '', start: formatDuration(deletingSegment?.segment.startOffsetMs ?? 0, true), duration: formatDuration(deletingSegment?.segment.durationMs ?? 0) })" :action-label="t('views.common.moveToTrash')" :busy="segmentDeleting" :error="segmentDeleteError" danger @update:model-value="!$event && !segmentDeleting && (deletingSegment = undefined)" @confirm="deleteSegment" />
    <ConfirmDialog v-if="auth.canManageRecordings" :model-value="cancelPermanentOpen" :title="t('shell.retention.cancelPermanent')" :message="t('shell.retention.cancelPermanentConfirm', { title: detail.session.title || t('components.library.unnamedSession') })" :action-label="t('shell.retention.cancelPermanent')" danger :busy="retentionBusy" :error="retentionError" @update:model-value="!$event && !retentionBusy && (cancelPermanentOpen = false)" @confirm="savePermanent(false)" />
    <SessionConfigurationDialog v-if="auth.canConfigureRecordings" v-model="configurationOpen" :session-id="id" @saved="onConfigurationSaved" />
    <SessionGroupDialog v-if="groupOpen && auth.canManageRecordings && isFinished" v-model="groupOpen" :session="detail.session" @grouped="router.replace(`/sessions/${$event.id}`)" />
    <SessionSplitDialog v-if="splitOpen && auth.canManageRecordings && isFinished" v-model="splitOpen" :session-id="id" :segments="detail.segments" @split="router.replace(`/streamers/${detail.session.streamerId}`)" />
    <v-dialog v-model="maintenanceOpen" max-width="580" :aria-label="maintenanceKind === 'Repair' ? t('views.session.repair') : t('views.session.merge')"><ContentCard :title="maintenanceKind === 'Repair' ? t('views.session.repair') : t('views.session.merge')"><div class="maintenance-segments"><v-checkbox v-for="(segment, index) in detail.segments.filter(item => item.isClosed)" :key="segment.id" v-model="selectedSegments" :value="segment.id" :label="t('views.session.segmentDescription', { number: String(index + 1).padStart(2, '0'), start: formatDuration(segment.startOffsetMs, true), size: formatBytes(segment.committedBytes) })" /></div><v-alert v-if="maintenanceError" type="error" class="mt-4">{{ maintenanceError }}</v-alert><template #actions><v-btn variant="text" @click="maintenanceOpen = false">{{ t('views.common.cancel') }}</v-btn><v-btn color="primary" :loading="maintenanceBusy" :disabled="!selectedSegments.length" @click="createMaintenance">{{ t('views.session.createJob') }}</v-btn></template></ContentCard></v-dialog>
    <v-snackbar v-model="jobCreated" color="surface-variant">{{ t('views.session.jobCreated') }}<template #actions><v-btn color="primary" to="/jobs">{{ t('views.session.view') }}</v-btn></template></v-snackbar>
    <v-snackbar v-model="segmentTrashed" color="surface-variant">{{ t('views.session.segmentTrashed') }}<template #actions><v-btn color="primary" to="/trash">{{ t('views.trash.title') }}</v-btn></template></v-snackbar>
  </v-defaults-provider>
</template>

<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { computed, mergeProps, nextTick, onBeforeUnmount, onMounted, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { MediaAPI } from '@/apis/MediaAPI'
import { useAuthStore } from '@/stores/auth'
import { useAction } from '@/composables/useAction'
import { usePolling } from '@/composables/usePolling'
import { useSessionTimeline } from '@/composables/useSessionTimeline'
import type { RecordingSegment, SessionDetail, Streamer } from '@/models/media'
import type { PlaybackLoadOptions } from '@/models/playbackLoadOptions'
import type { SessionBookmark } from '@/models/sessionBookmark'
import type { SessionConfiguration } from '@/models/sessionConfiguration'
import type { ClipSelection } from '@/models/clipSelection'
import { useSessionBookmarks } from '@/composables/useSessionBookmarks'
import { clampOffset } from '@/utilities/timeline'
import { removeSessionSegment, sessionMediaChanged } from '@/utilities/sessionSegments'
import { resolveClipRange } from '@/utilities/clipSelection'
import { formatBytes, formatDate, formatDuration, formatNumber } from '@/utilities/format'
import TranscodeDialog from '@/components/library/TranscodeDialog.vue'
import MediaPlayer from '@/components/player/MediaPlayer.vue'
import SessionPlaybackSidebar from '@/components/player/SessionPlaybackSidebar.vue'
import ClipDialog from '@/components/player/ClipDialog.vue'
import SessionDownloadDialog from '@/components/player/SessionDownloadDialog.vue'
import PlaybackBookmarkActions from '@/components/player/PlaybackBookmarkActions.vue'
import SessionBookmarkList from '@/components/player/SessionBookmarkList.vue'
import SessionBookmarkDialog from '@/components/player/SessionBookmarkDialog.vue'
import ConfirmDialog from '@/components/common/ConfirmDialog.vue'
import ContentCard from '@/components/common/ContentCard.vue'
import StatusChip from '@/components/common/StatusChip.vue'
import SessionExpiry from '@/components/library/SessionExpiry.vue'
import SessionGroupDialog from '@/components/library/SessionGroupDialog.vue'
import SessionSplitDialog from '@/components/library/SessionSplitDialog.vue'
import SessionConfigurationDialog from '@/components/library/SessionConfigurationDialog.vue'

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
const requestedMarkerFileId = computed(() => typeof route.query.liveMarkerFileId === 'string' && route.query.liveMarkerFileId ? route.query.liveMarkerFileId : undefined)
const player = ref<InstanceType<typeof MediaPlayer>>()
const playbackLayout = ref<HTMLElement>()
const playbackOverlayDefaults = computed(() =>
{
  const attach = playbackLayout.value
  return {
    VDialog: { attach, VMenu: { attach }, VTooltip: { attach } },
    VMenu: { attach },
    VTooltip: { attach },
  }
})
const clipOpen = ref(false)
const downloadOpen = ref(false)
const transcodeOpen = ref(false)
const configurationOpen = ref(false)
const cancelPermanentOpen = ref(false)
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
const deletingSegment = ref<{ segment: RecordingSegment; number: string }>()
const segmentTrashed = ref(false)
const bookmarksEditable = computed(() => detail.value?.session.isHidden !== true)
const { bookmarks, loading: bookmarksLoading, saving: bookmarkSaving, error: bookmarksError, refresh: refreshBookmarks, create: createBookmark, update: updateBookmark, remove: removeBookmark, clearError: clearBookmarksError } = useSessionBookmarks(() => id, () => !!detail.value)
const groupOpen = ref(false)
const splitOpen = ref(false)
const jobCreated = ref(false)
const maintenanceOpen = ref(false)
const maintenanceKind = ref<'Repair' | 'Merge'>('Repair')
const selectedSegments = ref<string[]>([])
const { busy, error, run } = useAction()
const { busy: retentionBusy, error: retentionError, run: runRetention } = useAction()
const { busy: maintenanceBusy, error: maintenanceError, run: runMaintenance } = useAction()
const { busy: segmentDeleting, error: segmentDeleteError, run: runSegmentDelete } = useAction()
const isLive = computed(() => detail.value?.session.state === 'Recording')
const { timeline, loading: timelineLoading, error: timelineError, refresh: refreshTimeline } = useSessionTimeline(() => id, () => !!detail.value?.session.hasDanmu, isLive)
const isFinished = computed(() => !!detail.value?.session.endedAtUtc && !isLive.value)
const showSegmentActions = computed(() => auth.canManageRecordings && !detail.value?.session.isHidden)
const segmentDeletionUnavailable = computed(() =>
{
  if (!isFinished.value || detail.value?.segments.some(segment => !segment.isClosed))
  {
    return t('views.session.segmentDeleteRecording')
  }
  return (detail.value?.segments.length ?? 0) <= 1 ? t('views.session.segmentDeleteLast') : ''
})
const loadWindow = (value: number, options?: PlaybackLoadOptions) => MediaAPI.playback(id, value, options)
let detailRequest = 0
let disposed = false
usePolling(async () => { if (!segmentDeleting.value && !retentionBusy.value) { await refreshSession() } }, 10000)
onBeforeUnmount(() => { disposed = true; detailRequest++ })
onMounted(async () =>
{
  await run(async () =>
  {
    streamers.value = await MediaAPI.streamers()
  })
})
async function refreshSession(): Promise<void>
{
  if (disposed) { return }
  const request = ++detailRequest
  await run(async () =>
  {
    const updated = await MediaAPI.session(id).catch(value =>
    {
      if (!disposed && request === detailRequest) { throw value }
      return undefined
    })
    if (!updated || disposed || request !== detailRequest) { return }
    const changed = detail.value && sessionMediaChanged(detail.value, updated)
    detail.value = updated
    if (changed) { await refreshMedia() }
  })
}
async function refreshMedia(): Promise<void>
{
  clipSelection.value = {}
  selectedSegments.value = selectedSegments.value.filter(segmentId => detail.value?.segments.some(segment => segment.id === segmentId))
  await nextTick()
  if (disposed) { return }
  await Promise.all([player.value?.reload(clampOffset(offsetMs.value, detail.value?.durationMs ?? 0)), refreshTimeline()])
}
function confirmSegmentDelete(segment: RecordingSegment, index: number): void
{
  if (!showSegmentActions.value || segmentDeletionUnavailable.value || segmentDeleting.value) { return }
  segmentDeleteError.value = ''
  deletingSegment.value = { segment, number: String(index + 1).padStart(2, '0') }
}
async function deleteSegment(): Promise<void>
{
  const target = deletingSegment.value
  if (!target || !showSegmentActions.value || segmentDeleting.value) { return }
  if (segmentDeletionUnavailable.value)
  {
    segmentDeleteError.value = segmentDeletionUnavailable.value
    return
  }
  detailRequest++
  await runSegmentDelete(async () =>
  {
    await MediaAPI.deleteSegment(id, target.segment.id)
    if (disposed) { return }
    detailRequest++
    if (detail.value) { detail.value = removeSessionSegment(detail.value, target.segment.id) }
    deletingSegment.value = undefined
    segmentTrashed.value = true
    await refreshMedia()
    await refreshSession()
  })
}
async function setPermanent(): Promise<void>
{
  if (!auth.canManageRecordings || !detail.value || retentionBusy.value) { return }
  retentionError.value = ''
  if (detail.value.session.isPermanent)
  {
    cancelPermanentOpen.value = true
    return
  }
  await savePermanent(true)
}
async function savePermanent(isPermanent: boolean): Promise<void>
{
  if (!auth.canManageRecordings || !detail.value || retentionBusy.value || (!isPermanent && !cancelPermanentOpen.value)) { return }
  await runRetention(async () =>
  {
    const updated = await MediaAPI.setSessionPermanent(id, isPermanent)
    if (disposed) { return }
    detailRequest++
    if (detail.value)
    {
      detail.value = { ...detail.value, session: { ...detail.value.session, isPermanent: updated.isPermanent, retentionStartedAtUtc: updated.retentionStartedAtUtc, expiresAtUtc: updated.expiresAtUtc } }
    }
    cancelPermanentOpen.value = false
  })
}
function onConfigurationSaved(configuration: SessionConfiguration): void
{
  if (detail.value)
  {
    detail.value = {
      ...detail.value,
      session: {
        ...detail.value.session,
        translationDanmuPatternOverride: configuration.translationDanmuPatternOverride,
        configurationRevision: configuration.revision,
      },
    }
  }
}
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
.playback-layout { --playback-sidebar-width: 360px; display: grid; grid-template-columns: minmax(0, 1fr) var(--playback-sidebar-width); gap: 24px; align-items: start; }
.playback-feed { position: sticky; top: calc(var(--v-layout-top, 0px) + 24px); height: calc(100dvh - var(--v-layout-top, 0px) - 120px); }
.playback-main { min-width: 0; }
.playback-layout:fullscreen { width: 100%; height: 100%; grid-template-columns: minmax(0, 1fr) min(var(--playback-sidebar-width), 40vw); grid-template-rows: minmax(0, 1fr); gap: 0; align-items: stretch; background: rgb(var(--v-theme-background)); overflow: hidden; }
.playback-layout:fullscreen .playback-main { min-height: 0; }
.playback-layout:fullscreen .playback-main > :not(.playback-player) { display: none; }
.playback-layout:fullscreen .playback-feed { position: static; height: 100%; border-radius: 0; border-block: 0; border-right: 0; }
.session-meta { display: flex; flex-direction: column; gap: 10px; padding-top: 16px; }
.session-meta h1 { margin: 0; font-size: 1.55rem; font-weight: 600; line-height: 1.4; letter-spacing: -.025em; overflow-wrap: anywhere; }
.session-details { display: flex; align-items: center; flex-wrap: wrap; gap: 12px 20px; }
.session-info { display: flex; flex: 1 1 360px; align-items: center; flex-wrap: wrap; gap: 8px 16px; min-width: 0; }
.session-info-item { display: inline-flex; align-items: center; gap: 6px; color: rgba(var(--v-theme-on-surface), .65); font-size: .75rem; line-height: 1.5; white-space: nowrap; }
.session-actions { display: flex; flex-shrink: 0; align-items: center; gap: 8px; margin-left: auto; }
.session-segments :deep(.v-expansion-panels) { border-radius: inherit; }
.session-segments-title { min-height: 56px; padding: var(--ui-card-header-padding) var(--ui-card-padding); font-size: var(--ui-card-title-size); font-weight: 600; line-height: 1.5; }
.session-segments :deep(.v-expansion-panel-text__wrapper) { padding: 0 var(--ui-card-padding) 4px; }
.session-segments :deep(.v-table) { border-top: 1px solid rgba(var(--v-theme-on-surface), .07); }
.segment-action { width: 1%; text-align: center; white-space: nowrap; }
.maintenance-segments { max-height: 380px; overflow-y: auto; }
@media (min-width: 1600px) { .playback-layout { --playback-sidebar-width: 390px; } }
@media (max-width: 1200px) { .playback-layout { grid-template-columns: minmax(0, 1fr); } .playback-feed { position: static; height: calc(100dvh - var(--v-layout-top, 0px) - 48px); } }
</style>
