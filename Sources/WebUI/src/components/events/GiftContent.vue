<template>
  <div class="gift-content"><span v-if="giftName">{{ giftName }} <b class="gift-quantity">×{{ formatNumber(eventQuantity(event)) }}</b></span><span v-else>{{ event.text || t('components.events.gift') }}</span><span v-if="event.amount !== null && event.amount !== undefined" class="gift-amount mono">{{ formatMoney(event.amount) }}</span></div>
</template>

<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { computed } from 'vue'
import type { RecordedEvent } from '@/models/media'
import { formatMoney, formatNumber } from '@/utilities/format'
import { eventQuantity, textValue } from '@/utilities/eventPresentation'

const { t } = useI18n()
const props = defineProps<{ event: RecordedEvent }>()
const giftName = computed(() => textValue(props.event.payload?.giftName))
</script>

<style scoped>.gift-content { display: flex; flex-wrap: wrap; gap: 8px 12px; justify-content: space-between; }.gift-quantity { color: rgb(var(--v-theme-primary)); font-weight: 600; }.gift-amount { color: rgb(var(--v-theme-primary)); font-size: .73rem; }</style>
