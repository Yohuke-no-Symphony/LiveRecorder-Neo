<template>
  <div>
    <div v-if="title || $slots.filters || (selectionEnabled && sessions.length)" class="session-library-header mb-4" :class="{ 'session-library-header-titled': title }">
      <h2 v-if="title" class="section-heading">{{ title }}</h2>
      <SessionLibraryToolbar
        v-if="$slots.filters || (selectionEnabled && sessions.length)"
        class="session-library-toolbar"
        :selection-enabled="selectionEnabled && !!sessions.length"
        :selection-mode="selectionMode"
        :selected-count="selectedIds.length"
        :selectable-count="selectableIds.length"
        :selection-limit="MAX_SELECTION"
        :busy="busy"
        :can-transcode="auth.isAdmin"
        @start="selectionMode = true"
        @select-loaded="selectLoaded"
        @cancel="cancelSelection"
        @trash="prepareTrash(selectedIds)"
        @transcode="prepareTranscode(selectedIds)"
      >
        <slot name="filters" />
      </SessionLibraryToolbar>
    </div>
    <v-alert v-if="error && !confirmOpen && !pendingCancellation" type="error" class="mb-4 deletion-error">{{ error }}</v-alert>
    <div v-if="sessions.length" class="media-grid">
      <SessionCard v-for="session in sessions" :key="session.id" :session="session" :streamer-name="streamerNames?.[session.streamerId]" :allow-actions="actionsAllowed" :selection-mode="selectionMode" :selected="selectedIds.includes(session.id)" :can-select="canSelect(session)" :disabled="busy" @actions="openActions(session, $event)" @toggle="toggle(session)" />
    </div>
    <v-menu v-model="menuOpen" :target="menuPosition" location="bottom start" context-menu>
      <v-list v-if="menuSession && actionsAllowed" density="compact">
        <v-list-item v-if="auth.canViewAnalytics" :title="t('components.library.analyze')" prepend-icon="mdi-chart-timeline-variant" @click="analyze" />
        <v-list-item v-if="selectionEnabled" :title="t(selectedIds.includes(menuSession.id) ? 'components.library.deselectSession' : 'components.library.selectSession', { title: menuSession.title || t('components.library.unnamedSession') })" prepend-icon="mdi-checkbox-marked-outline" :disabled="!canSelect(menuSession) || busy" @click="selectFromMenu" />
        <v-list-item v-if="auth.canManageRecordings" :title="t(menuSession.isPermanent ? 'shell.retention.cancelPermanent' : 'shell.retention.keepPermanent')" prepend-icon="mdi-shield-check-outline" :disabled="busy" @click="setPermanent" />
        <v-list-item v-if="auth.isAdmin" :title="t('transcoding.transcode')" prepend-icon="mdi-video-box" :disabled="busy || !canTrash(menuSession)" @click="prepareTranscode(menuTrashIds)" />
        <v-divider v-if="auth.canManageRecordings" />
        <v-list-item v-if="auth.canManageRecordings" :title="menuTrashIds.length > 1 ? t('components.library.trashSelected') : t('components.library.moveToTrash')" prepend-icon="mdi-delete-outline" base-color="error" :disabled="!canTrash(menuSession) || busy" @click="prepareTrash(menuTrashIds)" />
      </v-list>
    </v-menu>
    <TranscodeDialog v-if="auth.isAdmin" v-model="transcodeOpen" :session-ids="transcodeIds" @created="transcodeNotice = true" />
    <v-snackbar v-model="transcodeNotice">{{ t('transcoding.created') }}<template #actions><v-btn to="/jobs" color="primary">{{ t('transcoding.viewJobs') }}</v-btn></template></v-snackbar>
    <ConfirmDialog :model-value="confirmOpen" :title="t('components.library.trashTitle')" :message="t('components.library.trashConfirm', { count: formatNumber(pendingIds.length) })" :action-label="t('components.library.moveToTrash')" danger :busy="busy" :error="error" @update:model-value="setConfirmationOpen" @confirm="trash" />
    <ConfirmDialog v-if="auth.canManageRecordings" :model-value="!!pendingCancellation" :title="t('shell.retention.cancelPermanent')" :message="t('shell.retention.cancelPermanentConfirm', { title: pendingCancellation?.title || t('components.library.unnamedSession') })" :action-label="t('shell.retention.cancelPermanent')" danger :busy="busy" :error="error" @update:model-value="!$event && !busy && (pendingCancellation = undefined)" @confirm="pendingCancellation && savePermanent(pendingCancellation.id, false)" />
    <v-snackbar v-model="noticeOpen" color="surface-variant">{{ t('components.library.trashed', { count: formatNumber(trashedCount) }) }}<template #actions><v-btn color="primary" to="/admin/trash">{{ t('components.library.viewTrash') }}</v-btn></template></v-snackbar>
  </div>
</template>

<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import { MediaAPI } from '@/apis/MediaAPI'
import { useAction } from '@/composables/useAction'
import type { BroadcastSession } from '@/models/media'
import type { SessionTrashResult } from '@/models/sessionLibrary'
import { useAuthStore } from '@/stores/auth'
import { formatNumber } from '@/utilities/format'
import ConfirmDialog from '@/components/common/ConfirmDialog.vue'
import TranscodeDialog from './TranscodeDialog.vue'
import SessionCard from './SessionCard.vue'
import SessionLibraryToolbar from './SessionLibraryToolbar.vue'

const MAX_SELECTION = 200
const props = withDefaults(defineProps<{ sessions: BroadcastSession[]; title?: string; streamerNames?: Record<string, string>; allowSelection?: boolean }>(), { allowSelection: true })
const emit = defineEmits<{ removed: [ids: string[]]; retentionChanged: [session: BroadcastSession] }>()
const { t } = useI18n()
const router = useRouter()
const auth = useAuthStore()
const { busy, error, run } = useAction()
const selectionMode = ref(false)
const selectedIds = ref<string[]>([])
const menuOpen = ref(false)
const menuPosition = ref<[number, number]>([0, 0])
const menuId = ref('')
const confirmOpen = ref(false)
const pendingCancellation = ref<BroadcastSession>()
const pendingIds = ref<string[]>([])
const noticeOpen = ref(false)
const trashedCount = ref(0)
const transcodeOpen = ref(false)
const transcodeIds = ref<string[]>([])
const transcodeNotice = ref(false)
const actionsAllowed = computed(() => auth.canManageRecordings || auth.canViewAnalytics)
const selectionEnabled = computed(() => auth.canManageRecordings && props.allowSelection)
const selectableIds = computed(() => props.sessions.filter(canTrash).map(session => session.id))
const menuSession = computed(() => props.sessions.find(session => session.id === menuId.value))
const menuTrashIds = computed(() => selectionEnabled.value && selectedIds.value.includes(menuId.value) ? [...selectedIds.value] : [menuId.value])

function canTrash(session: BroadcastSession): boolean
{
  return !!session.endedAtUtc && session.state.toLowerCase() !== 'recording'
}

function canSelect(session: BroadcastSession): boolean
{
  return selectionEnabled.value && canTrash(session) && (selectedIds.value.includes(session.id) || selectedIds.value.length < MAX_SELECTION)
}

function toggle(session: BroadcastSession): void
{
  if (!auth.canManageRecordings || busy.value || !canSelect(session)) { return }
  selectionMode.value = true
  selectedIds.value = selectedIds.value.includes(session.id)
    ? selectedIds.value.filter(id => id !== session.id)
    : [...selectedIds.value, session.id]
}

function selectLoaded(): void
{
  if (!selectionEnabled.value || busy.value)
  {
    return
  }
  selectedIds.value = selectableIds.value.slice(0, MAX_SELECTION)
}

function cancelSelection(): void
{
  if (busy.value) { return }
  selectionMode.value = false
  selectedIds.value = []
}

function openActions(session: BroadcastSession, event: MouseEvent | KeyboardEvent): void
{
  if (!actionsAllowed.value || busy.value) { return }
  menuId.value = session.id
  if (event.type === 'contextmenu' && event instanceof MouseEvent)
  {
    menuPosition.value = [event.clientX, event.clientY]
  }
  else
  {
    const bounds = (event.currentTarget as HTMLElement).getBoundingClientRect()
    menuPosition.value = [bounds.left, bounds.bottom]
  }
  menuOpen.value = true
}

function analyze(): void
{
  if (!auth.canViewAnalytics || !menuSession.value) { return }
  menuOpen.value = false
  void router.push({ path: '/analytics', query: { sessionId: menuSession.value.id } })
}

async function setPermanent(): Promise<void>
{
  const session = menuSession.value
  if (!auth.canManageRecordings || !session || busy.value) { return }
  menuOpen.value = false
  error.value = ''
  if (session.isPermanent)
  {
    pendingCancellation.value = session
    return
  }
  await savePermanent(session.id, true)
}

async function savePermanent(id: string, isPermanent: boolean): Promise<void>
{
  if (!auth.canManageRecordings || busy.value || (!isPermanent && pendingCancellation.value?.id !== id)) { return }
  await run(async () =>
  {
    const updated = await MediaAPI.setSessionPermanent(id, isPermanent)
    emit('retentionChanged', updated)
    pendingCancellation.value = undefined
  })
}

function selectFromMenu(): void
{
  if (!menuSession.value) { return }
  toggle(menuSession.value)
  menuOpen.value = false
}

function prepareTranscode(ids: readonly string[]): void
{
  if (!auth.isAdmin || busy.value) { return }
  const eligible = new Set(selectableIds.value)
  transcodeIds.value = [...new Set(ids)].filter(id => eligible.has(id)).slice(0, MAX_SELECTION)
  if (!transcodeIds.value.length) { return }
  menuOpen.value = false
  transcodeOpen.value = true
}

function prepareTrash(ids: readonly string[]): void
{
  if (!auth.canManageRecordings || busy.value) { return }
  const eligible = new Set(selectableIds.value)
  pendingIds.value = [...new Set(ids)].filter(id => eligible.has(id)).slice(0, MAX_SELECTION)
  if (!pendingIds.value.length) { return }
  menuOpen.value = false
  error.value = ''
  confirmOpen.value = true
}

function retainFailures(ids: string[]): void
{
  if (!auth.canManageRecordings) { return }
  if (selectionEnabled.value)
  {
    selectedIds.value = [...new Set([...ids, ...selectedIds.value])].slice(0, MAX_SELECTION)
    selectionMode.value = true
  }
  pendingIds.value = ids
}

function setConfirmationOpen(open: boolean): void
{
  if (!busy.value) { confirmOpen.value = open }
}

async function trash(): Promise<void>
{
  if (!auth.canManageRecordings || busy.value || !pendingIds.value.length) { return }
  const ids = [...pendingIds.value]
  const eligible = new Set(selectableIds.value)
  if (ids.some(id => !eligible.has(id)))
  {
    pendingIds.value = ids.filter(id => eligible.has(id))
    error.value = t('components.library.selectionChanged')
    if (!pendingIds.value.length) { confirmOpen.value = false }
    return
  }
  const titles = new Map(props.sessions.map(session => [session.id, session.title || t('components.library.unnamedSession')]))
  const results = await run(async (): Promise<SessionTrashResult[]> =>
  {
    if (ids.length === 1)
    {
      await MediaAPI.deleteSession(ids[0]!)
      return [{ sessionId: ids[0]!, success: true }]
    }
    return await MediaAPI.trashSessions(ids)
  })
  if (!results)
  {
    retainFailures(ids)
    return
  }
  const returned = new Map(results.map(result => [result.sessionId, result]))
  const successful = ids.filter(id => returned.get(id)?.success === true)
  const failed = ids.filter(id => !successful.includes(id))
  selectedIds.value = selectedIds.value.filter(id => !successful.includes(id))
  if (successful.length)
  {
    emit('removed', successful)
    trashedCount.value = successful.length
    noticeOpen.value = true
  }
  if (failed.length)
  {
    retainFailures(failed)
    error.value = failed.map(id => t('components.library.trashFailure', { title: titles.get(id) || id, message: returned.get(id)?.error || t('components.common.operationFailed') })).join('\n')
    return
  }
  confirmOpen.value = false
  pendingIds.value = []
  if (!selectedIds.value.length) { selectionMode.value = false }
}

watch(selectableIds, ids =>
{
  const eligible = new Set(ids)
  selectedIds.value = selectedIds.value.filter(id => eligible.has(id))
})
watch(selectionEnabled, enabled =>
{
  if (enabled)
  {
    return
  }
  selectionMode.value = false
  selectedIds.value = []
})
watch(() => auth.canManageRecordings, allowed =>
{
  if (allowed) { return }
  menuOpen.value = false
  confirmOpen.value = false
  pendingCancellation.value = undefined
  selectionMode.value = false
  selectedIds.value = []
  pendingIds.value = []
})
</script>

<style scoped>
.session-library-header { display: flex; flex-wrap: wrap; align-items: center; gap: 8px 16px; }
.session-library-header .section-heading { min-width: 0; margin: 0; }
.session-library-toolbar { flex: 1 1 auto; min-width: 0; }
.session-library-header-titled .session-library-toolbar { flex: 0 1 auto; margin-inline-start: auto; justify-content: flex-end; }
.deletion-error { white-space: pre-line; }
</style>
