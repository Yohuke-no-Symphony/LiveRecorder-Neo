<template>
  <ContentCard :title="t('contributions.title')" class="analytics-contributions">
    <template #suffix><span class="text-caption muted">{{ rangeLabel }}</span></template>
    <template #header-actions>
      <div class="contribution-options contribution-view-options" role="group" :aria-label="t('contributions.views')">
        <v-btn data-contribution-view="events" :aria-pressed="view === 'events'"
               :variant="view === 'events' ? 'tonal' : 'text'" size="small" @click="view = 'events'">{{
          t('contributions.details') }}</v-btn>
        <v-btn data-contribution-view="users" :aria-pressed="view === 'users'"
               :variant="view === 'users' ? 'tonal' : 'text'" size="small" @click="view = 'users'">{{
          t('contributions.grouped') }}</v-btn>
      </div>
    </template>
    <div v-if="view === 'events'" class="contribution-toolbar">
      <div class="contribution-options" role="group" :aria-label="t('contributions.categories')">
        <v-btn v-for="option in categories" :key="option.value" :data-contribution-kind="option.value"
               :aria-pressed="kind === option.value" :variant="kind === option.value ? 'tonal' : 'text'"
               :color="kind === option.value ? option.color : undefined" size="small" @click="kind = option.value">
          <v-icon :icon="option.icon" size="18" class="mr-2"/>{{ t(option.label) }}
        </v-btn>
      </div>
    </div>
    <dl v-if="page && view === 'events'" class="contribution-stats">
      <div>
        <dt>{{ t('contributions.records') }}</dt>
        <dd>{{ formatNumber(page.summary.count) }}</dd>
      </div>
      <div>
        <dt>{{ t('contributions.senders') }}</dt>
        <dd>{{ formatNumber(page.summary.users) }}</dd>
      </div>
      <div>
        <dt>{{ t('contributions.quantity') }}</dt>
        <dd>{{ formatNumber(page.summary.quantity) }}</dd>
      </div>
      <div>
        <dt>{{ t(kind === 'RedPocket' ? 'contributions.battery' : 'contributions.amount') }}</dt>
        <dd>{{ kind === 'RedPocket' ? batteryValue(page.summary.totalBattery) : formatMoney(page.summary.amount) }}</dd>
      </div>
    </dl>
    <div v-else-if="page" class="contribution-user-totals text-caption muted">
      <span>{{ t('contributions.senders') }} <b>{{ formatNumber(page.summary.users) }}</b></span>
      <span>{{ t('contributions.records') }} <b>{{ formatNumber(page.summary.count) }}</b></span>
      <span>{{ t('contributions.knownTotal') }} <b>{{ formatMoney(page.summary.amount) }}</b></span>
    </div>
    <div class="contribution-notes text-caption muted">
      <template v-if="page && (view === 'users' || kind !== 'RedPocket')">
        <p v-if="page.summary.quotedAmount > 0">{{ t('contributions.quote') }} {{ formatMoney(page.summary.quotedAmount)
          }}</p>
        <p v-if="page.summary.unknownAmountCount > 0">{{ t('contributions.unknownAmounts', { count:
          formatNumber(page.summary.unknownAmountCount) }) }}</p>
      </template>
    </div>
    <v-progress-linear v-if="loading" indeterminate color="primary" :aria-label="t('contributions.loading')"
                       class="my-3"/>
    <v-alert v-if="error" type="error" variant="tonal" density="compact" class="my-3">
      <div class="contribution-error"><span>{{ error }}</span>
        <v-btn size="small" variant="text" :disabled="loading" @click="retry">{{ t('contributions.retry') }}</v-btn>
      </div>
    </v-alert>
    <div ref="userGrid" class="contribution-user-grid" :class="{ 'has-users': view === 'users' && page?.items.length }"
         :aria-busy="loading">
      <AnalyticsContributionUserCard v-for="item in view === 'users' ? page?.items ?? [] : []" :key="item.id"
                                     :user="item"/>
    </div>
    <div v-if="view === 'events' && page?.items.length" class="contribution-list" :aria-busy="loading">
      <article v-for="item in page.items" :key="item.id" class="contribution-row" :data-contribution-id="item.id">
        <div class="contribution-sender">
          <AnalyticsUserIdentity :user-id="item.userId" :user-name="item.userName"/>
          <a v-if="canPlay(item.offsetMs)" :href="playbackUrl(item.offsetMs)" class="contribution-time mono"
             target="_blank" rel="noopener noreferrer"
             :aria-label="`${t('replay.play')} ${formatDuration(item.offsetMs, true)}`">{{ formatDuration(item.offsetMs,
            true) }} ↗</a>
          <span v-else class="contribution-time mono muted">{{ formatDuration(item.offsetMs, true) }}</span>
        </div>
        <div class="contribution-body">
          <template v-if="item.event">
            <ScContent v-if="kind === 'SuperChat'" :event="item.event" :session-id="sessionId"/>
            <GiftContent v-else-if="kind === 'Gift'" :event="item.event"/>
            <GuardContent v-else-if="kind === 'Guard'" :event="item.event"/>
            <RedPocketContent v-else :event="item.event"/>
            <p
              v-if="kind !== 'RedPocket' && item.amount === null && item.quotedAmount === null && item.coinType !== 'silver'"
              class="text-caption muted mt-2">{{ t('contributions.amountUnknown') }}</p>
            <p v-if="item.coinType === 'silver'" class="text-caption muted mt-2">{{ t('contributions.freeGift') }}</p>
          </template>
          <p v-else>{{ item.text }}</p>
        </div>
      </article>
    </div>
    <EmptyState v-if="!page?.items.length && !loading && !error"
                :title="t(view === 'events' ? 'contributions.empty' : 'contributions.emptyUsers')"
                icon="mdi-gift-outline"/>
    <div v-if="page?.totalCount" class="contribution-pagination">
      <span class="text-caption muted mono" aria-live="polite">{{ pageLabel }}</span>
      <div v-if="hasPrevious || hasNext" class="contribution-options">
        <v-btn data-contribution-page="previous" variant="text" size="small" :disabled="loading || !hasPrevious"
               @click="previous">{{ t('contributions.previous') }}</v-btn>
        <v-btn data-contribution-page="next" variant="tonal" size="small" :disabled="loading || !hasNext" @click="next">{{ t('contributions.next') }}</v-btn>
      </div>
      <span v-else class="text-caption muted">{{ t(view === 'events' ? 'contributions.allLoaded' : 'contributions.allGroupsLoaded') }}</span>
    </div>
  </ContentCard>
</template>

<script setup lang="ts">
import {computed, ref, watch} from 'vue'
import {useRouter} from 'vue-router'
import {useI18n} from 'vue-i18n'
import type {AnalyticsRange} from '@/models/analyticsRange'
import type {ContributionFilter, ContributionKind, ContributionView} from '@/models/analyticsContributions'
import {useAnalyticsContributions} from '@/composables/useAnalyticsContributions'
import {formatDuration, formatMoney, formatNumber} from '@/utilities/format'
import ContentCard from '@/components/common/ContentCard.vue'
import EmptyState from '@/components/common/EmptyState.vue'
import AnalyticsUserIdentity from './AnalyticsUserIdentity.vue'
import AnalyticsContributionUserCard from './AnalyticsContributionUserCard.vue'
import ScContent from '@/components/events/ScContent.vue'
import GiftContent from '@/components/events/GiftContent.vue'
import GuardContent from '@/components/events/GuardContent.vue'
import RedPocketContent from '@/components/events/RedPocketContent.vue'

const props = defineProps<{ sessionId: string; durationMs: number; range?: AnalyticsRange; refreshKey?: number }>()
const {t} = useI18n()
const router = useRouter()
const kind = ref<ContributionKind>('SuperChat')
const view = ref<ContributionView>('events')
const requestedKind = computed<ContributionFilter>(() => view.value === 'users' ? 'All' : kind.value)
const userGrid = ref<HTMLElement>()
const gridColumns = ref(1)
const pageSize = computed(() => view.value === 'users' ? Math.max(gridColumns.value, Math.floor(50 / gridColumns.value) * gridColumns.value) : 50)
const categories: { value: ContributionKind; label: string; icon: string; color: string }[] = [
  {value: 'SuperChat', label: 'views.common.superChat', icon: 'mdi-message-text-outline', color: 'info'},
  {value: 'Gift', label: 'views.common.gifts', icon: 'mdi-gift-outline', color: 'primary'},
  {value: 'RedPocket', label: 'views.common.redPocket', icon: 'mdi-wallet-giftcard', color: 'error'},
  {value: 'Guard', label: 'views.common.guard', icon: 'mdi-shield-crown-outline', color: 'warning'},
]
const {page, cursor, loading, error, hasPrevious, hasNext, next, previous, retry} = useAnalyticsContributions(
  () => props.sessionId, requestedKind, view, () => props.range, () => props.refreshKey, pageSize,
)

watch(userGrid, (element, _previous, onCleanup) => {
  if (!element) {
    return
  }
  const updateColumns = (): void => {
    const columns = getComputedStyle(element).gridTemplateColumns.trim().split(/\s+/)
    if (columns.length > 0 && columns.every(column => /^\d+(?:\.\d+)?px$/.test(column))) {
      gridColumns.value = columns.length
    }
  }
  const observer = new ResizeObserver(updateColumns)
  observer.observe(element)
  updateColumns()
  onCleanup(() => observer.disconnect())
}, {flush: 'post'})
const rangeLabel = computed(() => props.range
  ? t('contributions.range', {
    from: formatDuration(props.range.fromMs, true),
    to: formatDuration(props.range.toMs, true)
  })
  : t('replay.fullSession'))
const pageLabel = computed(() => page.value ? t(view.value === 'events' ? 'contributions.page' : 'contributions.groupedPage', {
  from: formatNumber(cursor.value + 1),
  to: formatNumber(cursor.value + page.value.items.length),
  total: formatNumber(page.value.totalCount),
}) : '')

function batteryValue(value: number | null): string {
  return value === null ? t('contributions.unknown') : formatNumber(value)
}

function canPlay(offsetMs: number): boolean {
  return Number.isFinite(offsetMs) && offsetMs >= 0 && offsetMs <= props.durationMs
}

function playbackUrl(offsetMs: number): string {
  return router.resolve({path: `/sessions/${props.sessionId}`, query: {at: Math.floor(offsetMs)}}).href
}
</script>

<style scoped>
.contribution-toolbar, .contribution-options, .contribution-pagination, .contribution-error {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 8px;
}

.contribution-toolbar, .contribution-pagination, .contribution-error {
  justify-content: space-between;
}

.contribution-toolbar {
  gap: 12px 20px;
}

.contribution-stats {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: 16px;
  padding: 18px 0 14px;
}

.contribution-stats dt {
  color: rgba(var(--v-theme-on-surface), .55);
  font-size: .73rem;
}

.contribution-stats dd {
  margin: 6px 0 0;
  font-size: 1.3rem;
  font-weight: 600;
  font-variant-numeric: tabular-nums;
  overflow-wrap: anywhere;
}

.contribution-notes {
  display: flex;
  flex-wrap: wrap;
  gap: 6px 18px;
  margin-bottom: 8px;
}

.contribution-notes p {
  margin: 0;
}

.contribution-user-totals {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 6px 18px;
  margin: 2px 0 8px;
}

.contribution-user-totals b {
  margin-left: 4px;
  font-weight: 500;
  color: rgb(var(--v-theme-on-surface));
  font-variant-numeric: tabular-nums;
}

.contribution-user-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(min(300px, 100%), 1fr));
  gap: 12px;
}

.contribution-user-grid.has-users {
  margin: 12px 0;
}

.contribution-row {
  display: grid;
  grid-template-columns: minmax(140px, 220px) minmax(0, 1fr);
  gap: 18px;
  padding: 18px 0;
  border-top: 1px solid rgba(var(--v-theme-on-surface), .07);
}

.contribution-sender {
  display: flex;
  align-items: flex-start;
  flex-direction: column;
  gap: 8px;
  min-width: 0;
}

.contribution-sender :deep(.analytics-user-name) {
  max-width: 100%;
  white-space: normal;
  overflow: visible;
  overflow-wrap: anywhere;
}

.contribution-time {
  font-size: .73rem;
}

a.contribution-time {
  color: rgb(var(--v-theme-primary));
}

a.contribution-time:hover {
  text-decoration: underline;
}

a.contribution-time:focus-visible {
  outline: 2px solid rgb(var(--v-theme-primary));
  outline-offset: 3px;
}

.contribution-body {
  min-width: 0;
  font-size: .86rem;
  line-height: 1.6;
  overflow-wrap: anywhere;
}

.contribution-pagination {
  border-top: 1px solid rgba(var(--v-theme-on-surface), .07);
  padding-top: 14px;
}

@media (max-width: 600px) {
  .contribution-stats {
    grid-template-columns: repeat(2, minmax(0, 1fr));
    gap: 12px;
  }

  .contribution-row {
    grid-template-columns: minmax(0, 1fr);
    gap: 12px;
  }

  .contribution-sender {
    flex-direction: row;
    align-items: center;
    justify-content: space-between;
  }

  .contribution-sender > :first-child {
    flex: 1;
  }

  .contribution-time {
    flex-shrink: 0;
  }

  .contribution-options {
    gap: 4px;
  }

  .contribution-toolbar > .contribution-options {
    width: 100%;
  }
}
</style>
