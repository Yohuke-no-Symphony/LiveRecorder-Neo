<template>
  <div class="sc-content"><div v-if="event.amount !== null && event.amount !== undefined" class="sc-price mono">{{ formatMoney(event.amount) }}</div><div class="sc-text"><InlineEmoticons :content="textValue(event.payload?.content) || event.text" :emoticons="event.payload?.emoticons" :session-id="sessionId" :sequence="event.sequence" /></div></div>
</template>

<script setup lang="ts">
import type { RecordedEvent } from '@/models/media'
import { formatMoney } from '@/utilities/format'
import { textValue } from '@/utilities/eventPresentation'
import InlineEmoticons from '@/components/events/InlineEmoticons.vue'

defineProps<{ event: RecordedEvent; sessionId?: string }>()
</script>

<style scoped>.sc-content { border-left: 3px solid rgb(var(--v-theme-info)); border-radius: 4px; background: rgba(var(--v-theme-info), .065); padding: 8px 11px; }.sc-price { color: rgb(var(--v-theme-info)); font-weight: 650; margin-bottom: 6px; }.sc-text { white-space: pre-wrap; overflow-wrap: anywhere; }</style>
