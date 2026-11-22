<template>
  <div class="revenue-composition">
    <div v-if="total <= 0" class="empty-state py-10">{{ t('components.chart.empty') }}</div>
    <template v-else>
      <svg class="composition-chart" viewBox="0 0 200 200" role="img" :aria-label="t('views.analytics.revenueShare')">
        <circle cx="100" cy="100" r="76" class="composition-track"/>
        <circle v-for="part in parts" :key="part.kind" cx="100" cy="100" r="76" fill="none" stroke-width="22"
                pathLength="100" :stroke="`rgb(var(--v-theme-${part.color}))`"
                :stroke-dasharray="`${part.share * 100} ${100 - part.share * 100}`"
                :stroke-dashoffset="-part.offset * 100" transform="rotate(-90 100 100)">
          <title>{{ part.label }} · {{ formatMoney(part.amount) }} · {{ formatPercent(part.share) }}</title>
        </circle>
        <text x="100" y="97" class="composition-caption">{{ t('views.analytics.observedTotal') }}</text>
        <text x="100" y="120" class="composition-total">{{ formatMoney(total) }}</text>
      </svg>
      <dl class="composition-legend">
        <div v-for="part in parts" :key="part.kind" class="composition-row">
          <dt><span class="composition-dot" :style="{ background: `rgb(var(--v-theme-${part.color}))` }"/>{{ part.label
            }}
          </dt>
          <dd class="mono">{{ formatMoney(part.amount) }}<span class="muted">{{ formatPercent(part.share) }}</span></dd>
        </div>
      </dl>
    </template>
  </div>
</template>

<script setup lang="ts">
import {computed} from 'vue'
import {useI18n} from 'vue-i18n'
import type {Analytics} from '@/models/media'
import {formatMoney, formatNumber} from '@/utilities/format'
import {CATEGORY_PRESENTATION} from './categoryPresentation'

const props = defineProps<{ categories: Analytics['categories'] }>()
const {t} = useI18n()
const total = computed(() => props.categories.reduce((sum, category) => sum + category.amount, 0))
const parts = computed(() => {
  let offset = 0
  return props.categories.map(category => {
    const presentation = CATEGORY_PRESENTATION[category.kind]
    const share = total.value > 0 ? category.amount / total.value : 0
    const part = {...category, color: presentation.color, label: t(presentation.label), share, offset}
    offset += share
    return part
  })
})

function formatPercent(value: number): string {
  return formatNumber(value, {style: 'percent', maximumFractionDigits: 1})
}
</script>

<style scoped>
.composition-chart {
  display: block;
  width: min(100%, 230px);
  margin: 0 auto 24px;
  overflow: visible;
}

.composition-track {
  fill: none;
  stroke: rgba(var(--v-theme-on-surface), .05);
  stroke-width: 22;
}

.composition-caption, .composition-total {
  fill: rgb(var(--v-theme-on-surface));
  text-anchor: middle;
}

.composition-caption {
  font-size: 10px;
  opacity: .55;
}

.composition-total {
  font-size: 15px;
  font-weight: 600;
  font-variant-numeric: tabular-nums;
}

.composition-row {
  display: flex;
  justify-content: space-between;
  gap: 16px;
  align-items: center;
  margin: 15px 0;
  font-size: .82rem;
}

.composition-row dt {
  display: flex;
  align-items: center;
  gap: 10px;
}

.composition-row dd {
  display: flex;
  gap: 16px;
  margin: 0;
  text-align: right;
}

.composition-dot {
  width: 8px;
  height: 8px;
  border-radius: 50%;
  flex-shrink: 0;
}
</style>
