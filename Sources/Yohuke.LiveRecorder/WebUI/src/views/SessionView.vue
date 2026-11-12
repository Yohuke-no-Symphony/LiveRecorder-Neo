<template>
  <v-alert v-if="error" type="error" class="mb-6">{{ error }}</v-alert>
  <v-progress-linear v-if="!detail && busy" indeterminate color="primary" />
  <template v-if="detail">
    <div class="session-heading mb-6"><router-link :to="`/streamers/${detail.session.streamerId}`" class="text-body-2 muted"><v-icon icon="mdi-chevron-left" size="18" />{{ streamer?.name || '主播录播' }}</router-link><StatusChip :status="detail.session.state" /></div>
    <div class="playback-layout">
      <div class="playback-main">
        <MediaPlayer ref="player" v-model="offsetMs" :duration-ms="detail.durationMs" :gaps="detail.gaps" :is-live="isLive" :initial-offset-ms="initialOffset" :load-window="loadWindow" />
        <div class="session-meta">
          <h1>{{ detail.session.title }}</h1>
          <div class="d-flex align-center flex-wrap ga-3 mt-3"><span class="text-caption muted">{{ formatDate(detail.session.startedAtUtc) }}</span><span class="text-caption mono muted">{{ formatDuration(detail.durationMs, true) }}</span><span class="text-caption muted">{{ formatBytes(detail.session.totalBytes) }}</span><v-spacer /><v-btn color="primary" prepend-icon="mdi-content-cut" :disabled="detail.durationMs <= 0" @click="clipOpen = true">创建切片</v-btn><v-menu v-if="auth.isAdmin && isFinished"><template #activator="{ props: menuProps }"><v-btn v-bind="menuProps" icon="mdi-dots-horizontal" variant="text" aria-label="录播操作" /></template><v-list density="compact"><v-list-item title="归组场次" prepend-icon="mdi-folder-multiple-outline" @click="groupOpen = true" /><v-list-item title="拆分场次" prepend-icon="mdi-call-split" :disabled="detail.segments.length < 2" @click="splitOpen = true" /><v-divider /><v-list-item title="修复时间码" prepend-icon="mdi-wrench-outline" @click="openMaintenance('Repair')" /><v-list-item title="合并片段" prepend-icon="mdi-call-merge" @click="openMaintenance('Merge')" /></v-list></v-menu></div>
        </div>
        <v-expansion-panels v-if="detail.segments.length" variant="accordion" class="mt-6"><v-expansion-panel :title="`录制片段 · ${detail.segments.length}`"><v-expansion-panel-text><v-table density="comfortable"><thead><tr><th>片段</th><th>开始</th><th>时长</th><th>大小</th><th>状态</th></tr></thead><tbody><tr v-for="(segment, index) in detail.segments" :key="segment.id" class="clickable-row" @click="player?.seek(segment.startOffsetMs)"><td>{{ String(index + 1).padStart(2, '0') }}</td><td class="mono text-caption">{{ formatDuration(segment.startOffsetMs, true) }}</td><td class="mono text-caption">{{ formatDuration(segment.durationMs) }}</td><td class="mono text-caption">{{ formatBytes(segment.committedBytes) }}</td><td><StatusChip :status="segment.isClosed ? 'completed' : 'recording'" /></td></tr></tbody></v-table></v-expansion-panel-text></v-expansion-panel></v-expansion-panels>
      </div>
      <EventFeed :session-id="id" :offset-ms="offsetMs" :enabled="detail.session.hasDanmu" :is-live="isLive" @seek="player?.seek($event)" />
    </div>
    <ClipDialog v-model="clipOpen" :session-id="id" :session-title="detail.session.title" :current-ms="offsetMs" :duration-ms="detail.durationMs" @created="jobCreated = true" />
    <SessionGroupDialog v-if="groupOpen && auth.isAdmin && isFinished" v-model="groupOpen" :session="detail.session" @grouped="router.replace(`/sessions/${$event.id}`)" />
    <SessionSplitDialog v-if="splitOpen && auth.isAdmin && isFinished" v-model="splitOpen" :session-id="id" :segments="detail.segments" @split="router.replace(`/streamers/${detail.session.streamerId}`)" />
    <v-dialog v-model="maintenanceOpen" max-width="580"><v-card class="pa-6"><h2 class="text-h6 mb-5">{{ maintenanceKind === 'Repair' ? '修复时间码' : '合并片段' }}</h2><div class="maintenance-segments"><v-checkbox v-for="(segment, index) in detail.segments.filter(item => item.isClosed)" :key="segment.id" v-model="selectedSegments" :value="segment.id" :label="`片段 ${String(index + 1).padStart(2, '0')} · ${formatDuration(segment.startOffsetMs, true)} · ${formatBytes(segment.committedBytes)}`" /></div><v-alert v-if="maintenanceError" type="error" class="mt-4">{{ maintenanceError }}</v-alert><div class="d-flex justify-end ga-3 mt-6"><v-btn variant="text" @click="maintenanceOpen = false">取消</v-btn><v-btn color="primary" :loading="maintenanceBusy" :disabled="!selectedSegments.length" @click="createMaintenance">创建任务</v-btn></div></v-card></v-dialog>
    <v-snackbar v-model="jobCreated" color="surface-variant">后台任务已创建<template #actions><v-btn color="primary" to="/jobs">查看</v-btn></template></v-snackbar>
  </template>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { MediaAPI } from '@/apis/MediaAPI'
import { useAuthStore } from '@/stores/auth'
import { useAction } from '@/composables/useAction'
import { usePolling } from '@/composables/usePolling'
import type { SessionDetail, Streamer } from '@/models/media'
import { formatBytes, formatDate, formatDuration } from '@/utilities/format'
import MediaPlayer from '@/components/player/MediaPlayer.vue'
import EventFeed from '@/components/player/EventFeed.vue'
import ClipDialog from '@/components/player/ClipDialog.vue'
import StatusChip from '@/components/common/StatusChip.vue'
import SessionGroupDialog from '@/components/library/SessionGroupDialog.vue'
import SessionSplitDialog from '@/components/library/SessionSplitDialog.vue'

const route = useRoute()
const router = useRouter()
const auth = useAuthStore()
const id = String(route.params.id)
const detail = ref<SessionDetail>()
const streamers = ref<Streamer[]>([])
const streamer = computed(() => streamers.value.find(item => item.id === detail.value?.session.streamerId))
const offsetMs = ref(0)
const initialOffset = Math.max(0, Number(route.query.at) || 0)
const player = ref<InstanceType<typeof MediaPlayer>>()
const clipOpen = ref(false)
const groupOpen = ref(false)
const splitOpen = ref(false)
const jobCreated = ref(false)
const maintenanceOpen = ref(false)
const maintenanceKind = ref<'Repair' | 'Merge'>('Repair')
const selectedSegments = ref<string[]>([])
const { busy, error, run } = useAction()
const { busy: maintenanceBusy, error: maintenanceError, run: runMaintenance } = useAction()
const isLive = computed(() => detail.value?.session.state === 'Recording')
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
.playback-layout { display: grid; grid-template-columns: minmax(0, 1fr) 360px; gap: 24px; align-items: stretch; }
.playback-main { min-width: 0; }.session-meta { padding: 24px 1px 0; }.session-meta h1 { font-size: 1.55rem; font-weight: 600; line-height: 1.5; letter-spacing: -.025em; }
.maintenance-segments { max-height: 380px; overflow-y: auto; }
@media (min-width: 1600px) { .playback-layout { grid-template-columns: minmax(0, 1fr) 390px; } }
@media (max-width: 1200px) { .playback-layout { grid-template-columns: minmax(0, 1fr); } }
</style>
