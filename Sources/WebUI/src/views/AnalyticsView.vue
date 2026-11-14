<template>
  <PageHeader :title="t('views.analytics.title')">
    <AiAnalysisToolbar v-if="selectedId" :can-generate="canGenerateAi" :can-start="canStartAi" :has-result="hasAiResult" :can-generate-live-markers="canGenerateMarkers" :can-start-live-markers="canStartMarkers" :can-view-live-markers="Boolean(markerStatus || markerError)" @generate="generateAi" @details="aiDetailsOpen = true" @generate-live-markers="generateMarkers" @live-marker-details="markerDetailsOpen = true" />
    <v-btn v-if="selectedId" variant="text" icon="mdi-refresh" :aria-label="t('replay.refresh')" :disabled="busy" @click="loadSession(selectedId)" />
    <v-select :model-value="selectedId" :items="sessionOptions" :loading="sessionLoading" :label="t('views.analytics.selectRecording')" class="session-selector" density="compact" @update:model-value="selectSession">
      <template #append-item><v-list-item v-if="hasMore" :title="t('views.analytics.loadMore')" prepend-icon="mdi-chevron-down" :disabled="loadingMore" @click.stop="loadMore"><template #append><v-progress-circular v-if="loadingMore" size="18" width="2" indeterminate /></template></v-list-item></template>
    </v-select>
  </PageHeader>
  <AiAnalysisDetailsDialog v-if="selectedId" v-model="aiDetailsOpen" :status="aiStatus" :loading="aiLoading" :busy="aiBusy" :error="aiError" :ended="sessionEnded" :can-cancel="canCancelAi" @refresh="refreshAi" @cancel="cancelAi" />
  <AiAnalysisDetailsDialog v-if="selectedId" v-model="markerDetailsOpen" purpose="LiveMarkers" :status="markerStatus" :loading="markerLoading" :busy="markerBusy" :error="markerError" :ended="sessionEnded" :can-cancel="canCancelMarkers" @refresh="refreshMarkers" @cancel="cancelMarkers" />
  <v-alert v-if="error || sessionError || streamerError" type="error" class="mb-6">{{ error || sessionError || streamerError }}</v-alert>
  <v-progress-linear v-if="busy" indeterminate color="primary" class="mb-6" />
  <template v-if="analytics && selectedId">
    <p v-if="selectedRange" class="text-caption mb-3">{{ t('replay.selection', { from: formatDuration(selectedRange.fromMs), to: formatDuration(selectedRange.toMs) }) }}</p>
    <v-progress-linear v-if="rangeBusy" indeterminate color="primary" :aria-label="t('replay.loadingRange')" class="mb-4" />
    <div class="analytics-stats summary-grid mb-6">
      <v-card v-for="stat in stats" :key="stat.key" class="card-content" :data-stat="stat.key">
        <div class="stat-heading d-flex align-center ga-2">
          <div class="stat-label">{{ stat.label }}</div>
          <v-tooltip v-if="stat.description" :text="stat.description" location="top">
            <template #activator="{ props: tooltipProps }"><v-btn v-bind="tooltipProps" icon="mdi-information-outline" size="x-small" variant="text" :aria-label="stat.description" /></template>
          </v-tooltip>
        </div>
        <div class="stat-value mt-3">{{ stat.value }}</div>
      </v-card>
    </div>
    <div v-if="analytics.online" class="d-flex flex-wrap ga-4 text-caption muted mb-6">
      <span>{{ t('replay.coverage', { value: formatNumber(analytics.online.coverageRate, { style: 'percent', maximumFractionDigits: 1 }) }) }}</span>
      <span>{{ t('replay.observed', { duration: formatDuration(analytics.online.observedMs) }) }}</span>
    </div>
    <ContentCard v-if="fullAnalytics" class="mb-6"><RelationshipCharts :key="selectedId" :report="fullAnalytics" :duration-ms="durationMs" :session-id="selectedId" :range="selectedRange" :topics="topics" @range="selectRange" /></ContentCard>
    <AiAnalysisPanel v-model:show-partial="showPartialAi" :session-id="selectedId" :result="aiResult" :result-stale="aiResultStale" :has-both-results="hasBothAiResults" :loading="aiLoading" class="mb-6" @range="selectRange" />
    <h2 class="section-heading mb-4">{{ t('views.analytics.amountSources') }}</h2>
    <div class="category-grid mb-6"><CategoryRevenueCard v-for="category in analytics.categories" :key="category.kind" :category="category" /></div>
    <ContentCard class="mb-6"><ActivityChart :key="selectedId" :buckets="analytics.buckets" :duration-ms="durationMs" /></ContentCard>
    <div class="settings-grid mb-6">
      <ContentCard :title="t('views.analytics.revenueShare')"><RevenueComposition :categories="analytics.categories" /></ContentCard>
      <ContentCard :title="t('views.analytics.messageDistribution')">
        <div v-for="item in distribution" :key="item.label" class="distribution-row"><span class="text-body-2">{{ item.label }}</span><div class="distribution-track"><div :style="{ width: `${item.count / maxCount * 100}%` }" :class="`bg-${item.color}`" /></div><span class="mono text-caption">{{ formatNumber(item.count) }}</span></div>
        <v-divider class="my-5" /><div class="text-caption muted">{{ t('views.analytics.coverageGaps', { count: formatNumber(analytics.coverageGaps) }) }}</div>
      </ContentCard>
    </div>
    <TranslationAnalytics :key="`${selectedId}:translations`" :report="analytics.translations" :duration-ms="durationMs" class="mt-6" />
    <UserRanking :key="`${selectedId}:ranking`" :rankings="analytics.userRankings" class="mt-6" />
  </template>
  <EmptyState v-else-if="!busy && !sessionLoading && !selectedId" :title="t('views.analytics.empty')" icon="mdi-chart-timeline-variant" />
</template>

<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import ContentCard from '@/components/common/ContentCard.vue'
import { computed, onMounted, onScopeDispose, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { MediaAPI } from '@/apis/MediaAPI'
import { useSessionPages } from '@/composables/useSessionPages'
import { useAiAnalysis } from '@/composables/useAiAnalysis'
import { useAiLiveMarkers } from '@/composables/useAiLiveMarkers'
import type { Analytics, BroadcastSession, Streamer } from '@/models/media'
import { formatDate, formatDuration, formatMoney, formatNumber } from '@/utilities/format'
import type { AnalyticsRange } from '@/models/analyticsRange'
import { clampAnalyticsRange } from '@/components/analytics/relationshipData'
import PageHeader from '@/components/common/PageHeader.vue'
import EmptyState from '@/components/common/EmptyState.vue'
import ActivityChart from '@/components/analytics/ActivityChart.vue'
import CategoryRevenueCard from '@/components/analytics/CategoryRevenueCard.vue'
import RevenueComposition from '@/components/analytics/RevenueComposition.vue'
import UserRanking from '@/components/analytics/UserRanking.vue'
import TranslationAnalytics from '@/components/analytics/TranslationAnalytics.vue'
import RelationshipCharts from '@/components/analytics/RelationshipCharts.vue'
import AiAnalysisPanel from '@/components/analytics/AiAnalysisPanel.vue'
import AiAnalysisToolbar from '@/components/analytics/AiAnalysisToolbar.vue'
import AiAnalysisDetailsDialog from '@/components/analytics/AiAnalysisDetailsDialog.vue'

const { t } = useI18n()
const route = useRoute()
const router = useRouter()
const { sessions, loading: sessionLoading, loadingMore, hasMore, error: sessionError, loadMore } = useSessionPages(() => ({}))
const streamers = ref<Streamer[]>([])
const selectedId = ref('')
const selectedSession = ref<BroadcastSession>()
const analytics = ref<Analytics>()
const fullAnalytics = ref<Analytics>()
const selectedRange = ref<AnalyticsRange>()
const appliedRange = ref<AnalyticsRange>()
const rangeBusy = ref(false)
const aiDetailsOpen = ref(false)
const markerDetailsOpen = ref(false)
const sessionEnded = computed(() => selectedSession.value?.id === selectedId.value && Boolean(selectedSession.value?.endedAtUtc))
const {
  status: aiStatus, loading: aiLoading, busy: aiBusy, error: aiError, showPartial: showPartialAi,
  canGenerate: canGenerateAi, canStart: canStartAi, canCancel: canCancelAi, hasResult: hasAiResult,
  hasBothResults: hasBothAiResults, result: aiResult, resultStale: aiResultStale, topics,
  refresh: refreshAi, generate: startAi, cancel: cancelAi,
} = useAiAnalysis(selectedId, sessionEnded)
const {
  status: markerStatus, loading: markerLoading, busy: markerBusy, error: markerError,
  canGenerate: canGenerateMarkers, canStart: canStartMarkers, canCancel: canCancelMarkers,
  refresh: refreshMarkers, generate: startMarkers, cancel: cancelMarkers,
} = useAiLiveMarkers(selectedId, sessionEnded)
const durationMs = ref(0)
const busy = ref(false)
const error = ref('')
const streamerError = ref('')
let generation = 0
let disposed = false
let rangeGeneration = 0
let rangeController: AbortController | undefined
let rangeTimer: ReturnType<typeof setTimeout> | undefined

const sessionOptions = computed(() =>
{
  const options = [...sessions.value]
  if (selectedSession.value && !options.some(session => session.id === selectedSession.value!.id))
  {
    options.unshift(selectedSession.value)
  }
  return options.map(session => ({ title: [streamers.value.find(streamer => streamer.id === session.streamerId)?.name, session.title || t('components.library.unnamedSession'), formatDate(session.startedAtUtc)].filter(Boolean).join(' · '), value: session.id }))
})
const stats = computed(() => analytics.value ? [
  { key: 'totalRevenue', label: t('views.analytics.observedTotal'), value: formatMoney(analytics.value.observedAmount) },
  { key: 'activeUsers', label: t('views.analytics.interactionUsers'), value: formatNumber(analytics.value.activeUsers) },
  { key: 'danmuCount', label: t('views.common.danmu'), value: formatNumber(analytics.value.danmuCount) },
  { key: 'giftQuantity', label: t('views.analytics.giftQuantity'), value: formatNumber(analytics.value.giftQuantity) },
  { key: 'payingUsers', label: t('views.analytics.payingUsers'), value: formatNumber(analytics.value.payingUsers), description: t('views.analytics.payingUsersExplanation') },
  { key: 'payingParticipation', label: t('views.analytics.payingParticipation'), value: formatNumber(analytics.value.activeUsers > 0 ? analytics.value.payingUsers / analytics.value.activeUsers : 0, { style: 'percent', maximumFractionDigits: 1 }), description: t('views.analytics.payingParticipationExplanation') },
  { key: 'peakOnline', label: t('replay.peak'), value: onlineValue(analytics.value.online?.peak), description: t('replay.peakHelp') },
  { key: 'averageOnline', label: t('replay.average'), value: onlineValue(analytics.value.online?.average), description: t('replay.averageHelp') },
] : [])
const distribution = computed(() => analytics.value ? [
  { label: t('views.common.danmu'), count: analytics.value.danmuCount, color: 'secondary' },
  { label: t('views.common.superChat'), count: analytics.value.superChatCount, color: 'info' },
  { label: t('views.common.gifts'), count: analytics.value.giftCount, color: 'primary' },
  { label: t('views.common.guard'), count: analytics.value.guardCount, color: 'warning' },
  { label: t('views.common.redPocket'), count: analytics.value.redPocketCount, color: 'error' },
] : [])
const maxCount = computed(() => Math.max(1, ...distribution.value.map(item => item.count)))

function onlineValue(value: number | null | undefined): string
{
  return value == null ? t('replay.noData') : formatNumber(value, { maximumFractionDigits: 1 })
}

function generateAi(): void
{
  if (canStartAi.value)
  {
    aiDetailsOpen.value = true
    void startAi()
  }
}

function generateMarkers(): void
{
  if (canStartMarkers.value)
  {
    markerDetailsOpen.value = true
    void startMarkers()
  }
}

function selectRange(value: AnalyticsRange | undefined): void
{
  const next = value ? clampAnalyticsRange(value.fromMs, value.toMs, durationMs.value) : undefined
  if (value && !next)
  {
    return
  }
  selectedRange.value = next
  const operation = ++rangeGeneration
  rangeController?.abort()
  clearTimeout(rangeTimer)
  rangeBusy.value = Boolean(next)
  error.value = ''
  if (!next)
  {
    analytics.value = fullAnalytics.value
    appliedRange.value = undefined
    return
  }
  const id = selectedId.value
  rangeTimer = setTimeout(async () =>
  {
    rangeController = new AbortController()
    try
    {
      const report = await MediaAPI.analytics(id, next, rangeController.signal)
      if (operation === rangeGeneration && !disposed && selectedId.value === id)
      {
        analytics.value = report
        appliedRange.value = next
      }
    }
    catch (value)
    {
      if (operation === rangeGeneration && !disposed && !(value instanceof DOMException && value.name === 'AbortError'))
      {
        error.value = value instanceof Error ? value.message : t('views.analytics.loadFailed')
        selectedRange.value = appliedRange.value
      }
    }
    finally
    {
      if (operation === rangeGeneration && !disposed)
      {
        rangeBusy.value = false
      }
    }
  }, 250)
}

function selectSession(id: string): void
{
  if (id && id !== selectedId.value)
  {
    void router.push({ path: '/analytics', query: { ...route.query, sessionId: id } })
  }
}
watch(() => route.query.sessionId, id =>
{
  selectedId.value = typeof id === 'string' && id ? id : sessions.value[0]?.id ?? ''
}, { immediate: true })
watch(() => sessions.value[0]?.id, id =>
{
  if (!selectedId.value && id)
  {
    selectedId.value = id
  }
})
async function loadSession(id: string): Promise<void>
{
  const operation = ++generation
  rangeGeneration++
  rangeController?.abort()
  clearTimeout(rangeTimer)
  selectedRange.value = undefined
  appliedRange.value = undefined
  rangeBusy.value = false
  fullAnalytics.value = undefined
  analytics.value = undefined
  selectedSession.value = undefined
  error.value = ''
  durationMs.value = 0
  busy.value = Boolean(id)
  if (!id)
  {
    return
  }
  try
  {
    const [result, detail] = await Promise.all([MediaAPI.analytics(id), MediaAPI.session(id)])
    if (operation === generation)
    {
      analytics.value = result
      fullAnalytics.value = result
      durationMs.value = detail.durationMs
      selectedSession.value = detail.session
    }
  }
  catch (value)
  {
    if (operation === generation)
    {
      error.value = value instanceof Error ? value.message : t('views.analytics.loadFailed')
    }
  }
  finally
  {
    if (operation === generation)
    {
      busy.value = false
    }
  }
}
watch(selectedId, id =>
{
  aiDetailsOpen.value = false
  markerDetailsOpen.value = false
  void loadSession(id)
}, { immediate: true })
onMounted(async () =>
{
  try
  {
    const result = await MediaAPI.streamers()
    if (!disposed)
    {
      streamers.value = result
    }
  }
  catch (value)
  {
    if (!disposed)
    {
      streamerError.value = value instanceof Error ? value.message : t('views.analytics.loadFailed')
    }
  }
})
onScopeDispose(() => { generation++; rangeGeneration++; disposed = true; clearTimeout(rangeTimer); rangeController?.abort() })
</script>

<style scoped>
.session-selector { min-width: 280px; max-width: 520px; }
.stat-heading { min-height: 24px; }
.analytics-stats { grid-template-columns: repeat(4, minmax(0, 1fr)); }
.category-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(260px, 100%), 1fr)); gap: 20px; }
.distribution-row { display: grid; grid-template-columns: minmax(0, 110px) minmax(30px, 1fr) auto; align-items: center; gap: 15px; margin: 22px 0; }
.distribution-row > span:last-child { text-align: right; }
.distribution-track { height: 5px; border-radius: 5px; background: rgba(var(--v-theme-on-surface), .04); overflow: hidden; }
.distribution-track > div { height: 100%; border-radius: 5px; }
@media (max-width: 960px) { .category-grid { gap: 12px; } }
@media (max-width: 1200px) { .analytics-stats { grid-template-columns: repeat(2, minmax(0, 1fr)); } }
@media (max-width: 600px) { .category-grid { grid-template-columns: 1fr; } .session-selector { min-width: 0; width: 100%; } .analytics-stats .stat-value { font-size: 1.5rem; } }
</style>
