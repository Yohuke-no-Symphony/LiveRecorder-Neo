<template>
  <div class="red-pocket-content">
    <div class="pocket-heading"><span>{{ textValue(data.danmu) || event.text || '红包' }}</span><span class="pocket-status">{{ hasResult ? '已开奖' : event.payload?.isDrawn === true ? '已开奖，暂无结果' : '待开奖' }}</span></div>
    <div v-if="awards.length" class="pocket-awards"><div v-for="(award, index) in awards" :key="index" class="pocket-award"><img v-if="safeImageUrl(award.imageUrl)" :src="safeImageUrl(award.imageUrl)" :alt="textValue(award.name)" loading="lazy" /><span>{{ textValue(award.name) || textValue(award.giftId) || '奖品' }}<span v-if="numberValue(award.quantity)"> ×{{ award.quantity }}</span></span></div></div>
    <div v-if="totalCount !== undefined || endTime" class="pocket-meta"><span v-if="totalCount !== undefined">{{ totalCount }} 份</span><span v-if="endTime">开奖 {{ formatDate(endTime) }}</span></div>
    <div v-if="hasResult && winners.length" class="pocket-winners"><div v-for="(winner, index) in winners" :key="index" class="pocket-winner"><img v-if="safeImageUrl(winner.avatarUrl)" :src="safeImageUrl(winner.avatarUrl)" alt="" class="winner-avatar" loading="lazy" /><span class="winner-name">{{ winner.isMystery === true ? '神秘人' : textValue(winner.userName) || (winner.uid ? `UID ${winner.uid}` : '访客') }}</span><span>{{ winnerAward(winner) }}<span v-if="numberValue(winner.quantity) && Number(winner.quantity) > 1"> ×{{ winner.quantity }}</span></span></div></div>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import type { RecordedEvent } from '@/models/media'
import { formatDate } from '@/utilities/format'
import { numberValue, objectValue, redPocketData, safeImageUrl, textValue } from '@/utilities/eventPresentation'
const props = defineProps<{ event: RecordedEvent }>()
const data = computed(() => redPocketData(props.event))
const hasResult = computed(() => data.value.hasResult === true)
const awards = computed(() => (Array.isArray(data.value.awards) ? data.value.awards : []).map(objectValue))
const winners = computed(() => hasResult.value ? (Array.isArray(data.value.winners) ? data.value.winners : []).map(objectValue) : [])
const totalCount = computed(() => numberValue(data.value.totalAwardCount))
const endTime = computed(() =>
{
  const seconds = numberValue(data.value.endTime)
  const date = seconds && seconds > 0 ? new Date(seconds * 1000) : undefined
  return date && Number.isFinite(date.getTime()) ? date.toISOString() : undefined
})
function winnerAward(winner: Record<string, unknown>): string
{
  return textValue(winner.giftName) || textValue(awards.value.find(award => award.giftId === winner.giftId)?.name) ||
    (numberValue(winner.batteryAmount) !== undefined ? `${winner.batteryAmount} 电池` : '') ||
    (numberValue(winner.intimacyAmount) !== undefined ? `${winner.intimacyAmount} 亲密度` : '')
}
</script>

<style scoped>.red-pocket-content { border: 1px solid rgba(var(--v-theme-error), .2); border-radius: 8px; padding: 10px; }.pocket-heading { display: flex; justify-content: space-between; flex-wrap: wrap; gap: 7px; color: rgb(var(--v-theme-error)); }.pocket-status { font-size: .65rem; white-space: nowrap; }.pocket-awards { display: flex; flex-wrap: wrap; gap: 8px 14px; margin-top: 9px; }.pocket-award { display: flex; align-items: center; gap: 6px; }.pocket-award img { width: 24px; height: 24px; object-fit: contain; }.pocket-meta { display: flex; flex-wrap: wrap; gap: 12px; color: rgba(var(--v-theme-on-surface), .4); font-size: .65rem; margin-top: 9px; }.pocket-winners { border-top: 1px solid rgba(var(--v-theme-on-surface), .07); margin-top: 10px; padding-top: 5px; }.pocket-winner { display: flex; align-items: center; gap: 6px; flex-wrap: wrap; margin-top: 7px; font-size: .7rem; }.winner-avatar { width: 20px; height: 20px; border-radius: 50%; object-fit: cover; }.winner-name { color: rgba(var(--v-theme-on-surface), .55); margin-right: auto; }</style>
