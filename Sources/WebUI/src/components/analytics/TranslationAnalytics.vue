<template>
  <section class="translation-analytics" :aria-label="t('views.analytics.translationsTitle')">
    <h2 class="section-heading mb-4">{{ t('views.analytics.translationsTitle') }}</h2>
    <div class="translation-summary mb-6">
      <v-card class="card-content" data-stat="translationCount">
        <div class="stat-label">{{ t('views.analytics.translationTotal') }}</div>
        <div class="stat-value mt-3">{{ formatNumber(report.count) }}</div>
      </v-card>
      <v-card class="card-content" data-stat="translationUsers">
        <div class="stat-label">{{ t('views.analytics.translationUsers') }}</div>
        <div class="stat-value mt-3">{{ formatNumber(report.users) }}</div>
      </v-card>
    </div>
    <ContentCard class="mb-6">
      <ActivityChart mode="translations" :buckets="report.buckets" :duration-ms="durationMs"/>
    </ContentCard>
    <v-card class="translation-ranking">
      <CardHeader :title="t('views.analytics.translationUsers')">
        <template #suffix><span class="text-caption muted">{{ t('views.analytics.leaderboardLimit') }}</span></template>
      </CardHeader>
      <v-table v-if="report.topUsers.length" density="comfortable">
        <thead>
        <tr>
          <th>{{ t('views.analytics.rank') }}</th>
          <th>{{ t('views.common.user') }}</th>
          <th class="text-end">{{ t('views.analytics.translationCount') }}</th>
        </tr>
        </thead>
        <tbody>
        <tr v-for="(user, index) in report.topUsers" :key="user.userId">
          <td class="mono text-caption muted">{{ formatNumber(index + 1) }}</td>
          <td class="py-3 text-body-2">
            <AnalyticsUserIdentity :user-id="user.userId" :user-name="user.userName"/>
          </td>
          <td class="translation-user-count mono text-caption text-end">{{ formatNumber(user.count) }}</td>
        </tr>
        </tbody>
      </v-table>
      <EmptyState v-else :title="t('views.analytics.noTranslationUsers')" icon="mdi-translate"/>
    </v-card>
  </section>
</template>

<script setup lang="ts">
import {useI18n} from 'vue-i18n'
import type {Analytics} from '@/models/media'
import {formatNumber} from '@/utilities/format'
import ContentCard from '@/components/common/ContentCard.vue'
import CardHeader from '@/components/common/CardHeader.vue'
import EmptyState from '@/components/common/EmptyState.vue'
import ActivityChart from '@/components/analytics/ActivityChart.vue'
import AnalyticsUserIdentity from '@/components/analytics/AnalyticsUserIdentity.vue'

defineProps<{ report: Analytics['translations']; durationMs: number }>()
const {t} = useI18n()
</script>

<style scoped>
.translation-summary {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: var(--ui-layout-gap);
}

.translation-summary > * {
  min-width: 0;
}

.translation-ranking :deep(tbody tr:last-child td) {
  border-bottom: none;
}

@media (max-width: 600px) {
  .translation-summary {
    gap: 12px;
  }

  .translation-summary .stat-value {
    font-size: 1.5rem;
  }
}
</style>
