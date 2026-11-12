<template>
  <div class="guard-content"><span>{{ level }} <b>×{{ eventQuantity(event) }}</b></span><span v-if="event.amount !== null && event.amount !== undefined" class="guard-amount mono"><span v-if="isQuote" class="quote-label">报价</span>{{ formatMoney(event.amount) }}</span></div>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import type { RecordedEvent } from '@/models/media'
import { formatMoney } from '@/utilities/format'
import { eventQuantity, guardLevelName, textValue } from '@/utilities/eventPresentation'
const props = defineProps<{ event: RecordedEvent }>()
const level = computed(() => guardLevelName(props.event.payload?.guardLevel) || textValue(props.event.payload?.guardLevelName) || props.event.text || '大航海')
const isQuote = computed(() => props.event.priceSource === 'ProtocolQuote')
</script>

<style scoped>.guard-content { display: flex; flex-wrap: wrap; gap: 8px 12px; justify-content: space-between; color: rgb(var(--v-theme-warning)); }.guard-content b { font-weight: 600; }.guard-amount { font-size: .73rem; }.quote-label { font-family: inherit; font-size: .65rem; margin-right: 5px; opacity: .75; }</style>
