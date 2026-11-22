<template>
  <article class="contribution-user-card" :data-contribution-user="user.userId" :data-contribution-id="user.id">
    <div class="user-card-heading">
      <AnalyticsUserIdentity :user-id="user.userId" :user-name="user.userName"/>
      <div class="user-card-total"><span>{{ t('contributions.knownTotal') }}</span><strong>{{
        formatMoney(user.knownAmount) }}</strong></div>
    </div>
    <dl class="user-card-categories">
      <div v-for="category in user.categories" :key="category.kind" class="user-card-category"
           :data-category="category.kind">
        <dt>
          <v-icon :icon="presentation[category.kind].icon" :color="presentation[category.kind].color" size="15"/>
          <span>{{ t(presentation[category.kind].label) }}</span><small :title="t('contributions.records')">{{
          formatNumber(category.count) }}</small></dt>
        <dd>
          <span v-if="category.kind === 'RedPocket'">{{ category.totalBattery === null && category.count > 0 ? t('contributions.unknown') : t('components.events.battery', { count: formatNumber(category.totalBattery ?? 0) }) }}</span>
          <span v-else>{{ formatMoney(category.knownAmount) }}</span>
          <span v-if="category.kind === 'Gift' || category.kind === 'Guard'" class="user-card-quantity">×{{ formatNumber(category.quantity) }}</span>
          <v-tooltip v-if="category.kind !== 'RedPocket' && category.unknownAmountCount > 0"
                     :text="t('contributions.unknownAmounts', { count: formatNumber(category.unknownAmountCount) })"
                     location="top">
            <template #activator="{ props: tooltipProps }"><span v-bind="tooltipProps" class="user-card-unknown"
                                                                 tabindex="0">+ {{ t('contributions.unknown') }}</span>
            </template>
          </v-tooltip>
        </dd>
        <p v-if="category.quotedAmount > 0" class="user-card-quote">{{ t('contributions.quote') }} {{
          formatMoney(category.quotedAmount) }}</p>
      </div>
    </dl>
    <details v-if="projectCategories.length" class="user-card-projects">
      <summary>{{ t('contributions.projects') }}</summary>
      <div v-for="category in projectCategories" :key="category.kind" class="user-project-category">
        <div v-for="(project, index) in category.projects"
             :key="`${category.kind}:${project.giftId}:${project.guardLevel}:${index}`" class="user-project">
          <span>{{ projectName(category.kind, project) }} <span
            class="muted">×{{ formatNumber(project.quantity) }}</span></span>
          <span class="mono">{{ formatMoney(project.knownAmount) }}<span v-if="project.unknownAmountCount > 0"
                                                                         class="muted"> + {{ t('contributions.unknown') }}</span></span>
          <span v-if="project.quotedAmount > 0" class="user-project-quote muted">{{ t('contributions.quote') }} {{ formatMoney(project.quotedAmount) }}</span>
        </div>
      </div>
    </details>
  </article>
</template>

<script setup lang="ts">
import {computed} from 'vue'
import {useI18n} from 'vue-i18n'
import type {
  AnalyticsContribution,
  AnalyticsContributionProject,
  ContributionKind
} from '@/models/analyticsContributions'
import {formatMoney, formatNumber} from '@/utilities/format'
import {guardLevelName} from '@/utilities/eventPresentation'
import AnalyticsUserIdentity from './AnalyticsUserIdentity.vue'

const props = defineProps<{ user: AnalyticsContribution }>()
const {t} = useI18n()
const presentation: Record<ContributionKind, { label: string; icon: string; color: string }> = {
  SuperChat: {label: 'views.common.superChat', icon: 'mdi-message-text-outline', color: 'info'},
  Gift: {label: 'views.common.gifts', icon: 'mdi-gift-outline', color: 'primary'},
  Guard: {label: 'views.common.guard', icon: 'mdi-shield-crown-outline', color: 'warning'},
  RedPocket: {label: 'views.common.redPocket', icon: 'mdi-wallet-giftcard', color: 'error'},
}
const projectCategories = computed(() => props.user.categories.filter(category => category.projects.length > 0))

function projectName(kind: ContributionKind, project: AnalyticsContributionProject): string {
  return kind === 'Guard' ? guardLevelName(project.guardLevel) || project.text || t('views.common.guard')
    : project.giftName || project.text || t('views.common.gifts')
}
</script>

<style scoped>
.contribution-user-card {
  min-width: 0;
  padding: 12px;
  border: 1px solid rgba(var(--v-theme-on-surface), .1);
  border-radius: 10px;
  background: rgba(var(--v-theme-on-surface), .015);
}

.user-card-heading {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 10px;
}

.user-card-heading :deep(.analytics-user-identity) {
  min-width: 0;
  --event-avatar-size: 28px;
  font-size: .84rem;
}

.user-card-heading :deep(.analytics-user-name) {
  max-width: 100%;
  white-space: normal;
  overflow: visible;
  overflow-wrap: anywhere;
}

.user-card-total {
  display: grid;
  gap: 2px;
  flex-shrink: 0;
  text-align: right;
}

.user-card-total > span {
  color: rgba(var(--v-theme-on-surface), .55);
  font-size: .62rem;
}

.user-card-total > strong {
  color: rgb(var(--v-theme-primary));
  font-size: 1rem;
  font-variant-numeric: tabular-nums;
  line-height: 1.35;
}

.user-card-categories {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 10px 12px;
  margin-top: 12px;
}

.user-card-category {
  min-width: 0;
}

.user-card-category dt {
  display: flex;
  align-items: center;
  gap: 4px;
  color: rgba(var(--v-theme-on-surface), .6);
  font-size: .68rem;
}

.user-card-category dt small {
  margin-left: auto;
  font-size: .6rem;
  white-space: nowrap;
}

.user-card-category dd {
  display: flex;
  flex-wrap: wrap;
  align-items: baseline;
  gap: 5px;
  margin: 3px 0 0;
  font-size: .83rem;
  font-variant-numeric: tabular-nums;
}

.user-card-quantity, .user-card-unknown, .user-card-quote {
  color: rgba(var(--v-theme-on-surface), .55);
  font-size: .64rem;
}

.user-card-quote {
  margin: 2px 0 0;
}

.user-card-projects {
  border-top: 1px solid rgba(var(--v-theme-on-surface), .07);
  margin-top: 10px;
  padding-top: 7px;
  font-size: .68rem;
}

.user-card-projects > summary {
  cursor: pointer;
  color: rgba(var(--v-theme-on-surface), .6);
}

.user-project-category {
  margin-top: 6px;
}

.user-project {
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto;
  gap: 3px 8px;
  padding: 4px 0;
  overflow-wrap: anywhere;
}

.user-project-quote {
  grid-column: 1 / -1;
  text-align: right;
  font-size: .62rem;
}
</style>
