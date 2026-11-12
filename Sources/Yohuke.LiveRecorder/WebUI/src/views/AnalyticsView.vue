<template>
  <PageHeader :title="t('views.analytics.title')"><v-select v-model="selectedId" :items="sessionOptions" :loading="sessionLoading" :label="t('views.analytics.selectRecording')" style="min-width: 280px; max-width: 480px" density="compact"><template #append-item><v-list-item v-if="hasMore" :title="t('views.analytics.loadMore')" prepend-icon="mdi-chevron-down" :disabled="loadingMore" @click.stop="loadMore"><template #append><v-progress-circular v-if="loadingMore" size="18" width="2" indeterminate /></template></v-list-item></template></v-select></PageHeader>
  <v-alert v-if="error || sessionError" type="error" class="mb-6">{{ error || sessionError }}</v-alert>
  <v-progress-linear v-if="busy" indeterminate color="primary" class="mb-6" />
  <template v-if="analytics && selectedId">
    <div class="analytics-stats mb-6"><v-card v-for="stat in stats" :key="stat.label" class="pa-6"><div class="stat-label">{{ stat.label }}</div><div class="stat-value mt-3">{{ stat.value }}</div></v-card></div>
    <v-card class="pa-6 mb-6"><div class="d-flex align-center justify-space-between mb-5"><h2 class="section-heading mb-0">{{ t('views.analytics.amountSources') }}</h2><v-menu><template #activator="{ props: infoProps }"><v-btn v-bind="infoProps" icon="mdi-information-outline" size="small" variant="text" :aria-label="t('views.analytics.amountDefinition')" /></template><v-card class="pa-4" max-width="360">{{ t('views.analytics.amountExplanation') }}</v-card></v-menu></div><dl class="amount-grid"><div v-for="row in amountRows" :key="row.label" class="amount-row"><dt>{{ row.label }}</dt><dd class="mono">{{ row.value }}</dd></div></dl></v-card>
    <v-card class="pa-6 mb-6"><ActivityChart :buckets="analytics.buckets" :duration-ms="durationMs" @seek="router.push(`/sessions/${selectedId}?at=${$event}`)" /></v-card>
    <div class="settings-grid">
      <v-card class="pa-6"><h2 class="section-heading">{{ t('views.analytics.messageDistribution') }}</h2><div v-for="item in distribution" :key="item.label" class="distribution-row"><span class="text-body-2">{{ item.label }}</span><div class="distribution-track"><div :style="{ width: `${item.count / maxCount * 100}%` }" :class="`bg-${item.color}`" /></div><span class="mono text-caption">{{ formatNumber(item.count) }}</span></div><v-divider class="my-5" /><div class="text-caption muted">{{ t('views.analytics.coverageGaps', { count: formatNumber(analytics.coverageGaps) }) }}</div></v-card>
      <v-card><div class="pa-6 pb-3"><h2 class="section-heading mb-0">{{ t('views.analytics.activeUsers') }}</h2></div><v-table density="comfortable"><thead><tr><th>{{ t('views.common.user') }}</th><th>{{ t('views.analytics.messages') }}</th><th>{{ t('views.analytics.observedAmount') }}</th></tr></thead><tbody><tr v-for="user in analytics.topUsers" :key="user.userId"><td class="py-3 text-body-2">{{ user.userName || t('views.analytics.guest') }}</td><td class="mono text-caption">{{ formatNumber(user.messages) }}</td><td class="mono text-caption">{{ formatMoney(user.paidAmount) }}</td></tr></tbody></v-table><EmptyState v-if="!analytics.topUsers.length" :title="t('views.analytics.noActiveUsers')" icon="mdi-account-group-outline" /></v-card>
    </div>
  </template>
  <EmptyState v-else-if="!busy && !sessionLoading && !sessions.length" :title="t('views.analytics.empty')" icon="mdi-chart-timeline-variant" />
</template>

<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { computed, onMounted, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { MediaAPI } from '@/apis/MediaAPI'
import { useAction } from '@/composables/useAction'
import { useSessionPages } from '@/composables/useSessionPages'
import type { Analytics, Streamer } from '@/models/media'
import { formatDate, formatMoney, formatNumber } from '@/utilities/format'
import PageHeader from '@/components/common/PageHeader.vue'
import EmptyState from '@/components/common/EmptyState.vue'
import ActivityChart from '@/components/analytics/ActivityChart.vue'

const { t } = useI18n()

const router = useRouter()
const { sessions, loading: sessionLoading, loadingMore, hasMore, error: sessionError, loadMore } = useSessionPages(() => ({}))
const streamers = ref<Streamer[]>([])
const selectedId = ref('')
const analytics = ref<Analytics>()
const durationMs = ref(0)
const { busy, error, run } = useAction()
const sessionOptions = computed(() => sessions.value.map(session => ({ title: `${streamers.value.find(streamer => streamer.id === session.streamerId)?.name || ''} · ${session.title} · ${formatDate(session.startedAtUtc)}`, value: session.id })))
const stats = computed(() => analytics.value ? [
  { label: t('views.analytics.observedTotal'), value: formatMoney(analytics.value.observedAmount) },
  { label: t('views.analytics.activeUsers'), value: formatNumber(analytics.value.activeUsers) },
  { label: t('views.common.danmu'), value: formatNumber(analytics.value.danmuCount) },
  { label: t('views.analytics.giftQuantity'), value: formatNumber(analytics.value.giftQuantity) },
] : [])
const amountRows = computed(() => analytics.value ? [
  { label: t('views.analytics.goldValue'), value: formatMoney(analytics.value.goldGiftValue) },
  { label: t('views.analytics.protocolPaid'), value: formatMoney(analytics.value.protocolPaidAmount) },
  { label: t('views.analytics.quoted'), value: formatMoney(analytics.value.quotedAmount) },
  { label: t('views.analytics.unknownPrice'), value: t('views.common.recordCount', { count: formatNumber(analytics.value.unknownAmountCount) }) },
  { label: t('views.analytics.unknownGifts'), value: t('views.common.giftCount', { count: formatNumber(analytics.value.unknownGiftQuantity) }) },
  { label: t('views.analytics.unidentifiedFinancialEvents'), value: t('views.common.recordCount', { count: formatNumber(analytics.value.unidentifiedFinancialEvents) }) },
] : [])
const distribution = computed(() => analytics.value ? [
  { label: t('views.common.danmu'), count: analytics.value.danmuCount, color: 'secondary' },
  { label: t('views.common.superChat'), count: analytics.value.superChatCount, color: 'info' },
  { label: t('views.common.gifts'), count: analytics.value.giftCount, color: 'primary' },
  { label: t('views.common.guard'), count: analytics.value.guardCount, color: 'warning' },
  { label: t('views.common.redPocket'), count: analytics.value.redPocketCount, color: 'error' },
] : [])
const maxCount = computed(() => Math.max(1, ...distribution.value.map(item => item.count)))
onMounted(() => { void run(async () => { streamers.value = await MediaAPI.streamers() }) })
watch(() => sessions.value[0]?.id, id => { if (!selectedId.value && id) { selectedId.value = id } })
let generation = 0
watch(selectedId, async id =>
{
  if (!id) { return }
  const operation = ++generation
  analytics.value = undefined
  await run(async () =>
  {
    const [result, detail] = await Promise.all([MediaAPI.analytics(id), MediaAPI.session(id)])
    if (operation === generation) { analytics.value = result; durationMs.value = detail.durationMs }
  })
})
</script>

<style scoped>.analytics-stats { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: 20px; }.amount-grid { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 20px 32px; }.amount-row dt { font-size: .75rem; color: rgba(var(--v-theme-on-surface), .5); }.amount-row dd { margin-top: 8px; font-size: 1rem; }.distribution-row { display: grid; grid-template-columns: minmax(0, 110px) minmax(30px, 1fr) auto; align-items: center; gap: 15px; margin: 20px 0; }.distribution-row > span:last-child { text-align: right; }.distribution-track { height: 5px; border-radius: 5px; background: rgba(var(--v-theme-on-surface), .04); overflow: hidden; }.distribution-track > div { height: 100%; min-width: 1px; border-radius: 5px; }@media(max-width: 960px) { .analytics-stats, .amount-grid { grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 20px 12px; } }</style>
