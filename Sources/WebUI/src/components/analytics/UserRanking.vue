<template>
  <v-card class="user-ranking">
    <CardHeader inline-actions class="ranking-header" :title="t('views.analytics.activeUsers')">
      <template #suffix><span class="text-caption muted">{{ t('views.analytics.leaderboardLimit') }}</span></template>
      <template #actions><v-select v-model="metric" :items="metricOptions" :label="t('views.analytics.rankBy')" density="compact" hide-details class="ranking-selector" /></template>
    </CardHeader>
    <p v-if="metric.startsWith('redPocket')" class="px-4 pb-3 text-caption muted">{{ t('views.analytics.redPocketExplanation') }}</p>
    <v-table density="comfortable">
      <thead><tr><th>{{ t('views.analytics.rank') }}</th><th>{{ t('views.common.user') }}</th><th class="text-end">{{ t(`views.analytics.ranking.${metric}`) }}</th><th class="text-end">{{ t('views.analytics.observedAmount') }}</th></tr></thead>
      <tbody>
        <tr v-for="(user, index) in users" :key="user.userId">
          <td class="mono text-caption muted">{{ formatNumber(index + 1) }}</td>
          <td class="py-3 text-body-2"><AnalyticsUserIdentity :user-id="user.userId" :user-name="user.userName" /></td>
          <td class="ranking-value mono text-caption text-end">{{ formatValue(user[metric]) }}</td>
          <td class="mono text-caption text-end">{{ formatMoney(user.paidAmount) }}</td>
        </tr>
      </tbody>
    </v-table>
    <EmptyState v-if="!users.length" :title="t('views.analytics.noActiveUsers')" icon="mdi-account-group-outline" />
  </v-card>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import type { Analytics } from '@/models/media'
import { formatMoney, formatNumber } from '@/utilities/format'
import EmptyState from '@/components/common/EmptyState.vue'
import CardHeader from '@/components/common/CardHeader.vue'
import AnalyticsUserIdentity from '@/components/analytics/AnalyticsUserIdentity.vue'

const METRICS = ['danmuCount', 'superChatAmount', 'superChatCount', 'giftAmount', 'giftQuantity', 'guardAmount', 'guardQuantity', 'redPocketAmount', 'redPocketCount'] as const
const props = defineProps<{ rankings: Analytics['userRankings'] }>()
const { t } = useI18n()
const metric = ref<typeof METRICS[number]>('danmuCount')
const metricOptions = computed(() => METRICS.map(value => ({ value, title: t(`views.analytics.ranking.${value}`) })))
const users = computed(() => props.rankings[metric.value] ?? [])
function formatValue(value: number | null): string
{
  if (value === null)
  {
    return t('views.analytics.unknown')
  }
  return metric.value.endsWith('Amount') ? formatMoney(value) : formatNumber(value)
}
</script>

<style scoped>
.ranking-selector { width: 260px; }
.user-ranking :deep(tbody tr:last-child td) { border-bottom: none; }
@media (max-width: 600px) { .ranking-header :deep(.card-header-actions) { width: 100%; }.ranking-selector { width: 100%; } }
</style>
