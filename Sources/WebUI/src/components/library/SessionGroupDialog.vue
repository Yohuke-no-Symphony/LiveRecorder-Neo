<template>
  <v-dialog v-model="opened" :persistent="busy" max-width="720" scrollable :aria-label="t('components.library.groupTitle')">
    <ContentCard :title="t('components.library.groupTitle')">
      <template #suffix><v-chip v-if="selectedIds.length" color="primary" size="small">{{ t('components.library.selected', { count: formatNumber(selectedIds.length) }) }}</v-chip></template>
      <div class="form-stack mb-5"><v-text-field v-model="title" :label="t('components.library.groupName')" :disabled="busy" maxlength="180" /><v-text-field v-model="search" :label="t('components.library.searchSessions')" prepend-inner-icon="mdi-magnify" clearable :disabled="busy" @keydown.enter="applySearch" /></div>
      <div v-if="auth.canConfigureRecordings" class="form-stack mb-5">
        <v-select v-model="configurationMode" :items="configurationChoices" :label="t('shell.recordingConfiguration.groupChoice')" :hint="t('shell.recordingConfiguration.groupHint')" persistent-hint :disabled="busy" />
        <v-text-field v-if="configurationMode === 'override'" v-model="configurationPattern" :label="t('shell.recordingConfiguration.pattern')" :disabled="busy" spellcheck="false" autocomplete="off" />
      </div>
      <v-alert v-if="error || pageError" type="error" class="mb-4">{{ error || pageError }}</v-alert>
      <v-progress-linear v-if="loading" indeterminate color="primary" />
      <div v-for="session in candidates" :key="session.id" class="group-candidate"><v-checkbox v-model="selectedIds" :value="session.id" :disabled="busy" :aria-label="t('components.library.selectSession', { title: session.title })" density="compact" /><div class="flex-grow-1" @click="toggle(session.id)"><div class="text-body-2">{{ session.title }}</div><div class="text-caption muted mt-1">{{ formatDate(session.startedAtUtc) }} · {{ formatDuration(duration(session)) }}</div></div></div>
      <LoadMoreButton v-if="!busy" :has-more="hasMore" :loading="loadingMore" @load="loadMore" />
      <EmptyState v-if="!loading && !candidates.length" :title="t('components.library.noCandidates')" />
      <template #actions><v-btn variant="text" :disabled="busy" @click="opened = false">{{ t('components.common.cancel') }}</v-btn><v-btn color="primary" :loading="busy" :disabled="busy || !selectedIds.length || (configurationMode === 'override' && !configurationPattern.trim())" @click="submit">{{ t('components.library.group') }}</v-btn></template>
    </ContentCard>
  </v-dialog>
</template>

<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import ContentCard from '@/components/common/ContentCard.vue'
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { MediaAPI } from '@/apis/MediaAPI'
import { useAction } from '@/composables/useAction'
import { useSessionPages } from '@/composables/useSessionPages'
import { useAuthStore } from '@/stores/auth'
import type { BroadcastSession } from '@/models/media'
import { formatDate, formatDuration, formatNumber } from '@/utilities/format'
import EmptyState from '@/components/common/EmptyState.vue'
import LoadMoreButton from '@/components/common/LoadMoreButton.vue'

const { t } = useI18n()
const auth = useAuthStore()

const opened = defineModel<boolean>({ required: true })
const props = defineProps<{ session: BroadcastSession }>()
const emit = defineEmits<{ grouped: [session: BroadcastSession] }>()
const title = ref(props.session.title)
const search = ref<string | null>('')
const searchQuery = ref('')
const selectedIds = ref<string[]>([])
const configurationMode = ref<'inherit' | 'global' | 'override'>('inherit')
const configurationPattern = ref(props.session.translationDanmuPatternOverride ?? '')
const configurationChoices = computed(() => [
  { value: 'inherit', title: t('shell.recordingConfiguration.groupInherit') },
  { value: 'global', title: t('shell.recordingConfiguration.groupGlobal') },
  { value: 'override', title: t('shell.recordingConfiguration.groupOverride') },
])
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
    const configuration = auth.canConfigureRecordings && configurationMode.value !== 'inherit'
      ? { translationDanmuPatternOverride: configurationMode.value === 'global' ? null : configurationPattern.value }
      : undefined
    const result = await MediaAPI.groupSessions(props.session.id, [...selectedIds.value], title.value.trim() || undefined, configuration)
    opened.value = false
    emit('grouped', result)
  })
}
onBeforeUnmount(() => { clearTimeout(searchTimer) })
</script>

<style scoped>.group-candidate { display: flex; align-items: center; gap: 8px; padding: 12px 0; border-bottom: 1px solid rgba(var(--v-theme-on-surface), .06); }.group-candidate > div:last-child { cursor: pointer; }</style>
