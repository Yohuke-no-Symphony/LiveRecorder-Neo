<template>
  <PageHeader :title="t('views.analytics.title')">
    <AiAnalysisToolbar v-if="selectedId" :can-generate="canGenerateAi" :can-start="canStartAi" :has-result="hasAiResult"
                       :can-generate-live-markers="canGenerateMarkers" :can-start-live-markers="canStartMarkers"
                       :can-view-live-markers="Boolean(markerStatus || markerError)" @generate="generateAi"
                       @details="aiDetailsOpen = true" @generate-live-markers="generateMarkers"
                       @live-marker-details="markerDetailsOpen = true"/>
    <AnalyticsExportButton v-if="selectedId && auth.canViewAnalytics" :session-id="selectedId" :range="appliedRange"
                           :duration-ms="durationMs" :disabled="busy || rangeBusy || !analytics"/>
    <v-btn v-if="selectedId" variant="text" icon="mdi-refresh" :aria-label="t('replay.refresh')" :disabled="busy"
           @click="loadSession(selectedId)"/>
    <v-btn variant="outlined" prepend-icon="mdi-folder-play-outline" append-icon="mdi-chevron-down"
           class="session-selector" :loading="sessionLoading" :title="selectedSessionLabel"
           :aria-label="t('views.analytics.selectRecording')" aria-haspopup="dialog" @click="sessionPickerOpen = true">
      <span class="session-selector-label">{{ selectedSessionLabel || t('views.analytics.selectRecording') }}</span>
    </v-btn>
  </PageHeader>
  <SessionPickerDialog v-model="sessionPickerOpen" :selected-id="selectedId" @select="selectSession"/>
  <AiAnalysisDetailsDialog v-if="selectedId" v-model="aiDetailsOpen" :status="aiStatus" :loading="aiLoading"
                           :busy="aiBusy" :error="aiError" :ended="sessionEnded" :can-cancel="canCancelAi"
                           @refresh="refreshAi" @cancel="cancelAi"/>
  <AiAnalysisDetailsDialog v-if="selectedId" v-model="markerDetailsOpen" purpose="LiveMarkers" :status="markerStatus"
                           :loading="markerLoading" :busy="markerBusy" :error="markerError" :ended="sessionEnded"
                           :can-cancel="canCancelMarkers" @refresh="refreshMarkers" @cancel="cancelMarkers"/>
  <v-alert v-if="error || sessionError || streamerError" type="error" class="mb-6">{{ error || sessionError ||
    streamerError }}
  </v-alert>
  <v-snackbar :model-value="Boolean(pinSaveError)" color="error"
              @update:model-value="pinSaveError = $event ? pinSaveError : ''">{{ pinSaveError }}
  </v-snackbar>
  <v-progress-linear v-if="busy" indeterminate color="primary" class="mb-6"/>
  <div v-if="analytics && selectedId" class="analytics-layout"
       :style="{ '--analytics-controls-height': `${rangePinned ? controlsHeight : 0}px`, '--analytics-navigation-height': `${navigationHeight}px` }">
    <div ref="navigationElement" class="analytics-navigation">
      <AnalyticsContents :key="`${selectedId}:contents`" :sections="sections"
                         :scroll-offset="(rangePinned ? controlsHeight : 0) + navigationHeight"/>
    </div>
    <div ref="controlsElement" class="analytics-controls" :class="{ 'is-pinned': rangePinned }">
      <AnalyticsRangeToolbar v-model:pinned="rangePinned" :pin-saving="auth.analyticsPreferenceSaving"
                             :range="selectedRange" :duration-ms="durationMs" :busy="rangeBusy" :report="fullAnalytics"
                             :timeline="timeline" :timeline-loading="timelineLoading" :timeline-error="timelineError"
                             @range="selectRange" @retry-timeline="refreshTimeline"/>
    </div>
    <section id="analytics-overview" class="analytics-section" tabindex="-1"
             :aria-label="t('views.analytics.overview')">
      <div class="analytics-stats summary-grid mb-6">
        <v-card v-for="stat in stats" :key="stat.key" class="card-content" :data-stat="stat.key">
          <div class="stat-heading d-flex align-center ga-2">
            <div class="stat-label">{{ stat.label }}</div>
            <v-tooltip v-if="stat.description" :text="stat.description" location="top">
              <template #activator="{ props: tooltipProps }">
                <v-btn v-bind="tooltipProps" icon="mdi-information-outline" size="x-small" variant="text"
                       :aria-label="stat.description"/>
              </template>
            </v-tooltip>
          </div>
          <div class="stat-value mt-3">{{ stat.value }}</div>
        </v-card>
      </div>
      <div v-if="analytics.online" class="d-flex flex-wrap ga-4 text-caption muted mb-6">
        <span>{{ t('replay.coverage', { value: formatNumber(analytics.online.coverageRate, { style: 'percent', maximumFractionDigits: 1 }) }) }}</span>
        <span>{{ t('replay.observed', { duration: formatDuration(analytics.online.observedMs) }) }}</span>
      </div>
    </section>
    <section id="analytics-relationships" class="analytics-section mb-6" tabindex="-1" :aria-label="t('replay.title')">
      <ContentCard v-if="fullAnalytics">
        <RelationshipCharts :key="selectedId" :report="fullAnalytics" :duration-ms="durationMs" :session-id="selectedId"
                            :range="selectedRange" :topics="topics" @range="selectRange"/>
      </ContentCard>
    </section>
    <section id="analytics-ai" class="analytics-section mb-6" tabindex="-1" :aria-label="t('replay.aiTitle')">
      <AiAnalysisPanel v-model:show-partial="showPartialAi" :session-id="selectedId" :result="aiResult"
                       :result-stale="aiResultStale" :has-both-results="hasBothAiResults" :loading="aiLoading"
                       @range="selectRange"/>
    </section>
    <section id="analytics-sources" class="analytics-section" tabindex="-1"
             :aria-label="t('views.analytics.amountSources')">
      <h2 class="section-heading mb-4">{{ t('views.analytics.amountSources') }}</h2>
      <div class="category-grid mb-6">
        <CategoryRevenueCard v-for="category in analytics.categories" :key="category.kind" :category="category"/>
      </div>
    </section>
    <section id="analytics-activity" class="analytics-section mb-6" tabindex="-1"
             :aria-label="t('views.analytics.activityTitle')">
      <ContentCard>
        <ActivityChart :key="selectedId" :buckets="analytics.buckets" :duration-ms="durationMs"/>
      </ContentCard>
    </section>
    <section id="analytics-distribution" class="analytics-section settings-grid mb-6" tabindex="-1"
             :aria-label="t('views.analytics.distributions')">
      <ContentCard :title="t('views.analytics.revenueShare')">
        <RevenueComposition :categories="analytics.categories"/>
      </ContentCard>
      <ContentCard :title="t('views.analytics.messageDistribution')">
        <div v-for="item in distribution" :key="item.label" class="distribution-row"><span class="text-body-2">{{ item.label }}</span>
          <div class="distribution-track">
            <div :style="{ width: `${item.count / maxCount * 100}%` }" :class="`bg-${item.color}`"/>
          </div>
          <span class="mono text-caption">{{ formatNumber(item.count) }}</span></div>
        <v-divider class="my-5"/>
        <div class="text-caption muted">{{ t('views.analytics.coverageGaps', { count:
          formatNumber(analytics.coverageGaps) }) }}
        </div>
      </ContentCard>
    </section>
    <section id="analytics-translations" class="analytics-section mt-6" tabindex="-1"
             :aria-label="t('views.analytics.translationsTitle')">
      <TranslationAnalytics :key="`${selectedId}:translations`" :report="analytics.translations"
                            :duration-ms="durationMs"/>
    </section>
    <section id="analytics-users" class="analytics-section mt-6" tabindex="-1"
             :aria-label="t('views.analytics.activeUsers')">
      <UserRanking :key="`${selectedId}:ranking`" :rankings="analytics.userRankings"/>
    </section>
    <section id="analytics-contributions" class="analytics-section mt-6" tabindex="-1"
             :aria-label="t('contributions.title')">
      <AnalyticsContributions :key="`${selectedId}:contributions`" :session-id="selectedId" :duration-ms="durationMs"
                              :range="appliedRange"/>
    </section>
  </div>
  <EmptyState v-else-if="!busy && !sessionLoading && !selectedId" :title="t('views.analytics.empty')"
              icon="mdi-chart-timeline-variant"/>
</template>

<script setup lang="ts">
import {useI18n} from 'vue-i18n'
import ContentCard from '@/components/common/ContentCard.vue'
import {computed, onMounted, onScopeDispose, ref, watch} from 'vue'
import {useRoute, useRouter} from 'vue-router'
import {MediaAPI} from '@/apis/MediaAPI'
import {useAuthStore} from '@/stores/auth'
import {useAiAnalysis} from '@/composables/useAiAnalysis'
import {useAiLiveMarkers} from '@/composables/useAiLiveMarkers'
import {useSessionTimeline} from '@/composables/useSessionTimeline'
import type {Analytics, BroadcastSession, Streamer} from '@/models/media'
import {formatDate, formatDuration, formatMoney, formatNumber} from '@/utilities/format'
import type {AnalyticsRange} from '@/models/analyticsRange'
import type {AnalyticsSection} from '@/models/analyticsSection'
import {clampAnalyticsRange} from '@/components/analytics/relationshipData'
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
import AnalyticsContents from '@/components/analytics/AnalyticsContents.vue'
import AnalyticsRangeToolbar from '@/components/analytics/AnalyticsRangeToolbar.vue'
import AnalyticsContributions from '@/components/analytics/AnalyticsContributions.vue'
import AnalyticsExportButton from '@/components/analytics/AnalyticsExportButton.vue'
import SessionPickerDialog from '@/components/library/SessionPickerDialog.vue'

const {t} = useI18n()
const auth = useAuthStore()
const route = useRoute()
const router = useRouter()
const sessionLoading = ref(false)
const sessionError = ref('')
const latestSession = ref<BroadcastSession>()
const sessionPickerOpen = ref(false)
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
  timeline,
  loading: timelineLoading,
  error: timelineError,
  refresh: refreshTimeline
} = useSessionTimeline(selectedId, () => Boolean(selectedId.value && fullAnalytics.value), () => !sessionEnded.value)
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
const controlsElement = ref<HTMLElement>()
const navigationElement = ref<HTMLElement>()
const controlsHeight = ref(100)
const navigationHeight = ref(40)
const pinSaveError = ref('')
const rangePinned = computed({
  get: () => auth.user?.analyticsRangePinned ?? true,
  set: value => {
    void saveRangePin(value)
  },
})

async function saveRangePin(value: boolean): Promise<void> {
  pinSaveError.value = ''
  try {
    await auth.setAnalyticsRangePinned(value)
  } catch {
    pinSaveError.value = t('replay.pinSaveFailed')
  }
}

let controlsObserver: ResizeObserver | undefined
const sections = computed<AnalyticsSection[]>(() => [
  {id: 'analytics-overview', title: t('views.analytics.overview')},
  {id: 'analytics-relationships', title: t('replay.title')},
  {id: 'analytics-ai', title: t('replay.aiTitle')},
  {id: 'analytics-sources', title: t('views.analytics.amountSources')},
  {id: 'analytics-activity', title: t('views.analytics.activityTitle')},
  {id: 'analytics-distribution', title: t('views.analytics.distributions')},
  {id: 'analytics-translations', title: t('views.analytics.translationsTitle')},
  {id: 'analytics-users', title: t('views.analytics.activeUsers')},
  {id: 'analytics-contributions', title: t('contributions.title')},
])

watch([controlsElement, navigationElement], ([controls, navigation]) => {
  controlsObserver?.disconnect()
  if (controls && navigation?.firstElementChild) {
    const directory = navigation.firstElementChild
    const measure = () => {
      controlsHeight.value = Math.ceil(controls.getBoundingClientRect().height)
      navigationHeight.value = Math.ceil(directory.getBoundingClientRect().height)
    }
    measure()
    if (typeof ResizeObserver !== 'undefined') {
      controlsObserver = new ResizeObserver(measure)
      controlsObserver.observe(controls)
      controlsObserver.observe(directory)
    }
  }
}, {flush: 'post'})

const selectedSessionLabel = computed(() => {
  const session = selectedSession.value
  return session ? [streamers.value.find(streamer => streamer.id === session.streamerId)?.name, session.title || t('components.library.unnamedSession'), formatDate(session.startedAtUtc)].filter(Boolean).join(' · ') : ''
})
const stats = computed(() => analytics.value ? [
  {key: 'totalRevenue', label: t('views.analytics.observedTotal'), value: formatMoney(analytics.value.observedAmount)},
  {key: 'activeUsers', label: t('views.analytics.interactionUsers'), value: formatNumber(analytics.value.activeUsers)},
  {key: 'danmuCount', label: t('views.common.danmu'), value: formatNumber(analytics.value.danmuCount)},
  {key: 'giftQuantity', label: t('views.analytics.giftQuantity'), value: formatNumber(analytics.value.giftQuantity)},
  {
    key: 'payingUsers',
    label: t('views.analytics.payingUsers'),
    value: formatNumber(analytics.value.payingUsers),
    description: t('views.analytics.payingUsersExplanation')
  },
  {
    key: 'payingParticipation',
    label: t('views.analytics.payingParticipation'),
    value: formatNumber(analytics.value.activeUsers > 0 ? analytics.value.payingUsers / analytics.value.activeUsers : 0, {
      style: 'percent',
      maximumFractionDigits: 1
    }),
    description: t('views.analytics.payingParticipationExplanation')
  },
  {
    key: 'peakOnline',
    label: t('replay.peak'),
    value: onlineValue(analytics.value.online?.peak),
    description: t('replay.peakHelp')
  },
  {
    key: 'averageOnline',
    label: t('replay.average'),
    value: onlineValue(analytics.value.online?.average),
    description: t('replay.averageHelp')
  },
] : [])
const distribution = computed(() => analytics.value ? [
  {label: t('views.common.danmu'), count: analytics.value.danmuCount, color: 'secondary'},
  {label: t('views.common.superChat'), count: analytics.value.superChatCount, color: 'info'},
  {label: t('views.common.gifts'), count: analytics.value.giftCount, color: 'primary'},
  {label: t('views.common.guard'), count: analytics.value.guardCount, color: 'warning'},
  {label: t('views.common.redPocket'), count: analytics.value.redPocketCount, color: 'error'},
] : [])
const maxCount = computed(() => Math.max(1, ...distribution.value.map(item => item.count)))

function onlineValue(value: number | null | undefined): string {
  return value == null ? t('replay.noData') : formatNumber(value, {maximumFractionDigits: 1})
}

function generateAi(): void {
  if (canStartAi.value) {
    aiDetailsOpen.value = true
    void startAi()
  }
}

function generateMarkers(): void {
  if (canStartMarkers.value) {
    markerDetailsOpen.value = true
    void startMarkers()
  }
}

function selectRange(value: AnalyticsRange | undefined): void {
  const next = value ? clampAnalyticsRange(value.fromMs, value.toMs, durationMs.value) : undefined
  if (value && !next) {
    return
  }
  selectedRange.value = next
  const operation = ++rangeGeneration
  rangeController?.abort()
  clearTimeout(rangeTimer)
  rangeBusy.value = Boolean(next)
  error.value = ''
  if (!next) {
    analytics.value = fullAnalytics.value
    appliedRange.value = undefined
    return
  }
  const id = selectedId.value
  rangeTimer = setTimeout(async () => {
    rangeController = new AbortController()
    try {
      const report = await MediaAPI.analytics(id, next, rangeController.signal)
      if (operation === rangeGeneration && !disposed && selectedId.value === id) {
        analytics.value = report
        appliedRange.value = next
      }
    } catch (value) {
      if (operation === rangeGeneration && !disposed && !(value instanceof DOMException && value.name === 'AbortError')) {
        error.value = value instanceof Error ? value.message : t('views.analytics.loadFailed')
        selectedRange.value = appliedRange.value
      }
    } finally {
      if (operation === rangeGeneration && !disposed) {
        rangeBusy.value = false
      }
    }
  }, 250)
}

function selectSession(id: string): void {
  sessionPickerOpen.value = false
  if (id && id !== selectedId.value) {
    void router.push({path: '/analytics', query: {...route.query, sessionId: id}})
  }
}

watch(() => route.query.sessionId, id => {
  selectedId.value = typeof id === 'string' && id ? id : latestSession.value?.id ?? ''
  if (selectedId.value) {
    sessionError.value = ''
  } else {
    void loadLatestSession()
  }
}, {immediate: true})

async function loadLatestSession(): Promise<void> {
  if (sessionLoading.value) {
    return
  }
  sessionLoading.value = true
  sessionError.value = ''
  try {
    const sessions = await MediaAPI.sessions(undefined, undefined, undefined, 1)
    if (!disposed) {
      latestSession.value = sessions[0]
      const id = route.query.sessionId
      if (!(typeof id === 'string' && id)) {
        selectedId.value = latestSession.value?.id ?? ''
      }
    }
  } catch (value) {
    if (!disposed && !selectedId.value) {
      sessionError.value = value instanceof Error ? value.message : t('views.analytics.loadFailed')
    }
  } finally {
    if (!disposed) {
      sessionLoading.value = false
    }
  }
}

async function loadSession(id: string): Promise<void> {
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
  if (!id) {
    return
  }
  try {
    const [result, detail] = await Promise.all([MediaAPI.analytics(id), MediaAPI.session(id)])
    if (operation === generation) {
      analytics.value = result
      fullAnalytics.value = result
      durationMs.value = detail.durationMs
      selectedSession.value = detail.session
    }
  } catch (value) {
    if (operation === generation) {
      error.value = value instanceof Error ? value.message : t('views.analytics.loadFailed')
    }
  } finally {
    if (operation === generation) {
      busy.value = false
    }
  }
}

watch(selectedId, id => {
  aiDetailsOpen.value = false
  markerDetailsOpen.value = false
  void loadSession(id)
}, {immediate: true})
onMounted(async () => {
  try {
    const result = await MediaAPI.streamers()
    if (!disposed) {
      streamers.value = result
    }
  } catch (value) {
    if (!disposed) {
      streamerError.value = value instanceof Error ? value.message : t('views.analytics.loadFailed')
    }
  }
})
onScopeDispose(() => {
  generation++;
  rangeGeneration++;
  disposed = true;
  clearTimeout(rangeTimer);
  rangeController?.abort();
  controlsObserver?.disconnect()
})
</script>

<style scoped>
.analytics-layout {
  position: relative;
  --analytics-navigation-offset: calc(var(--analytics-navigation-height) + 8px);
}

.analytics-navigation {
  position: sticky;
  top: calc(var(--v-layout-top, 0px) + 12px);
  z-index: 11;
  margin-bottom: 8px;
}

.analytics-controls {
  position: relative;
  z-index: 10;
  margin-bottom: 24px;
}

.analytics-controls.is-pinned {
  position: sticky;
  top: calc(var(--v-layout-top, 0px) + var(--analytics-navigation-offset) + 12px);
}

.analytics-section {
  scroll-margin-top: calc(var(--v-layout-top, 0px) + var(--analytics-navigation-offset) + var(--analytics-controls-height) + 28px);
  min-width: 0;
}

.analytics-section:focus {
  outline: none;
}

@media (min-width: 1280px) {
  .analytics-layout {
    margin-left: 212px;
    --analytics-navigation-offset: 0px;
  }

  .analytics-navigation {
    position: absolute;
    right: calc(100% + 24px);
    top: 0;
    bottom: 0;
    width: 188px;
    margin: 0;
  }

  .analytics-navigation > .analytics-contents {
    position: sticky;
    top: calc(var(--v-layout-top, 0px) + 12px);
  }
}

@media (min-width: 2240px) {
  .analytics-layout {
    margin-left: 0;
  }
}

.session-selector {
  min-width: 0;
  max-width: 440px;
}

.session-selector :deep(.v-btn__content) {
  min-width: 0;
}

.session-selector-label {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.stat-heading {
  min-height: 24px;
}

.analytics-stats {
  grid-template-columns: repeat(4, minmax(0, 1fr));
}

.category-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(min(260px, 100%), 1fr));
  gap: 20px;
}

.distribution-row {
  display: grid;
  grid-template-columns: minmax(0, 110px) minmax(30px, 1fr) auto;
  align-items: center;
  gap: 15px;
  margin: 22px 0;
}

.distribution-row > span:last-child {
  text-align: right;
}

.distribution-track {
  height: 5px;
  border-radius: 5px;
  background: rgba(var(--v-theme-on-surface), .04);
  overflow: hidden;
}

.distribution-track > div {
  height: 100%;
  border-radius: 5px;
}

@media (max-width: 960px) {
  .category-grid {
    gap: 12px;
  }
}

@media (max-width: 1200px) {
  .analytics-stats {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
}

@media (max-width: 600px) {
  .category-grid {
    grid-template-columns: 1fr;
  }

  .session-selector {
    min-width: 0;
    width: 100%;
  }

  .analytics-stats .stat-value {
    font-size: 1.5rem;
  }
}
</style>
