<template>
  <v-card class="category-revenue-card card-content" :data-kind="category.kind">
    <div class="d-flex align-center ga-3 mb-4">
      <v-icon :icon="presentation.icon" :color="presentation.color" size="22"/>
      <h3 class="category-title">{{ t(presentation.label) }}</h3>
    </div>
    <div class="category-amount mono">{{ formatMoney(category.amount) }}</div>
    <dl class="category-details mt-5">
      <div>
        <dt>{{ t('views.analytics.sourceRecords') }}</dt>
        <dd>{{ formatNumber(category.count) }}</dd>
      </div>
      <div>
        <dt>{{ t('views.analytics.sourceQuantity') }}</dt>
        <dd>{{ formatNumber(category.quantity) }}</dd>
      </div>
      <div>
        <dt>{{ t('views.analytics.sourceUsers') }}</dt>
        <dd>{{ formatNumber(category.users) }}</dd>
      </div>
    </dl>
  </v-card>
</template>

<script setup lang="ts">
import {computed} from 'vue'
import {useI18n} from 'vue-i18n'
import type {Analytics} from '@/models/media'
import {formatMoney, formatNumber} from '@/utilities/format'
import {CATEGORY_PRESENTATION} from './categoryPresentation'

const props = defineProps<{ category: Analytics['categories'][number] }>()
const {t} = useI18n()
const presentation = computed(() => CATEGORY_PRESENTATION[props.category.kind])
</script>

<style scoped>
.category-title {
  font-size: .95rem;
  font-weight: 600;
}

.category-amount {
  font-size: 1.7rem;
  font-weight: 650;
  letter-spacing: -.03em;
}

.category-details {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 12px;
}

.category-details dt {
  font-size: .73rem;
  color: rgba(var(--v-theme-on-surface), .5);
}

.category-details dd {
  margin: 7px 0 0;
  font-size: .9rem;
  font-variant-numeric: tabular-nums;
}
</style>
