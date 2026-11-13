<template>
  <v-card class="user-ranking">
    <div class="ranking-header pa-6 pb-3">
      <div><h2 class="section-heading mb-1">{{ t('views.analytics.activeUsers') }}</h2><p class="text-caption muted">{{ t('views.analytics.leaderboardLimit') }}</p></div>
      <v-select v-model="metric" :items="metricOptions" :label="t('views.analytics.rankBy')" density="compact" hide-details class="ranking-selector" />
    </div>
    <p v-if="metric.startsWith('redPocket')" class="px-6 pb-3 text-caption muted">{{ t('views.analytics.redPocketExplanation') }}</p>
    <v-table density="comfortable">
      <thead><tr><th>{{ t('views.analytics.rank') }}</th><th>{{ t('views.common.user') }}</th><th class="text-end">{{ t(`views.analytics.ranking.${metric}`) }}</th><th class="text-end">{{ t('views.analytics.observedAmount') }}</th></tr></thead>
      <tbody>
        <tr v-for="(user, index) in users" :key="user.userId">
          <td class="mono text-caption muted">{{ formatNumber(index + 1) }}</td>
          <td class="py-3 text-body-2">{{ user.userName || t('views.analytics.guest') }}</td>
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
.ranking-header { display: flex; align-items: center; justify-content: space-between; gap: 24px; flex-wrap: wrap; }
.ranking-selector { flex: 0 1 260px; min-width: 200px; }
.user-ranking :deep(tbody tr:last-child td) { border-bottom: none; }
@media (max-width: 600px) { .ranking-selector { flex-basis: 100%; } }
</style>
