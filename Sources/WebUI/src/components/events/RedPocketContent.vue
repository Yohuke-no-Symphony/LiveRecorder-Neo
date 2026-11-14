<template>
  <div class="red-pocket-content">
    <div class="pocket-heading"><span>{{ textValue(data.danmu) || event.text || t('components.events.redPocket') }}</span><span class="pocket-status">{{ hasResult ? t('components.events.drawn') : event.payload?.isDrawn === true ? t('components.events.drawnWithoutResult') : t('components.events.pendingDraw') }}</span></div>
    <div v-if="awards.length" class="pocket-awards"><div v-for="(award, index) in awards" :key="index" class="pocket-award"><EventImage v-if="safeImageUrl(award.imageUrl)" :sources="[safeImageUrl(award.imageUrl)!]" :alt="textValue(award.name)" /><span>{{ textValue(award.name) || textValue(award.giftId) || t('components.events.award') }}<span v-if="numberValue(award.quantity)"> ×{{ formatNumber(Number(award.quantity)) }}</span></span></div></div>
    <div v-if="totalBattery !== undefined || totalCount !== undefined || endTime" class="pocket-meta"><span v-if="totalBattery !== undefined">{{ t('components.events.totalBattery', { count: formatNumber(totalBattery) }) }}</span><span v-if="totalCount !== undefined">{{ t('components.events.awardCount', { count: formatNumber(totalCount) }) }}</span><span v-if="endTime">{{ t('components.events.drawAt', { date: formatDate(endTime) }) }}</span></div>
    <div v-if="hasResult && winners.length" class="pocket-winners"><div v-for="(winner, index) in winners" :key="index" class="pocket-winner"><EventAvatar :sources="winnerSources(winner)" :name="winnerName(winner)" class="winner-avatar" /><span class="winner-name">{{ winnerName(winner) }}</span><span>{{ winnerAward(winner) }}</span></div></div>
  </div>
</template>

<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { computed } from 'vue'
import type { RecordedEvent } from '@/models/media'
import { formatDate, formatNumber } from '@/utilities/format'
import { numberValue, objectValue, redPocketData, safeImageUrl, textValue } from '@/utilities/eventPresentation'
import { audienceAvatarUrl } from '@/utilities/eventAssets'
import EventAvatar from '@/components/events/EventAvatar.vue'
import EventImage from '@/components/events/EventImage.vue'

const { t } = useI18n()
const props = defineProps<{ event: RecordedEvent }>()
const data = computed(() => redPocketData(props.event))
const hasResult = computed(() => data.value.hasResult === true)
const awards = computed(() => (Array.isArray(data.value.awards) ? data.value.awards : []).map(objectValue))
const winners = computed(() => hasResult.value ? (Array.isArray(data.value.winners) ? data.value.winners : []).map(objectValue) : [])
const totalBattery = computed(() => numberValue(data.value.totalBattery))
const totalCount = computed(() => numberValue(data.value.totalAwardCount))
const endTime = computed(() =>
{
  const seconds = numberValue(data.value.endTime)
  const date = seconds && seconds > 0 ? new Date(seconds * 1000) : undefined
  return date && Number.isFinite(date.getTime()) ? date.toISOString() : undefined
})
function winnerName(winner: Record<string, unknown>): string
{
  return winner.isMystery === true ? t('components.events.mystery') : textValue(winner.userName) || (winner.uid ? `UID ${winner.uid}` : t('components.events.guest'))
}
function winnerSources(winner: Record<string, unknown>): string[]
{
  const source = winner.isMystery === true ? undefined : audienceAvatarUrl(winner.uid)
  return source ? [source] : []
}
function winnerAward(winner: Record<string, unknown>): string
{
  const batteryAmount = numberValue(winner.batteryAmount)
  if (batteryAmount !== undefined)
  {
    return t('components.events.battery', { count: formatNumber(batteryAmount) })
  }
  const intimacyAmount = numberValue(winner.intimacyAmount)
  if (intimacyAmount !== undefined)
  {
    return t('components.events.intimacy', { count: formatNumber(intimacyAmount) })
  }
  const name = textValue(winner.giftName) || textValue(awards.value.find(award => award.giftId === winner.giftId)?.name)
  const quantity = numberValue(winner.quantity)
  return quantity !== undefined && quantity > 1 ? `${name} ×${formatNumber(quantity)}` : name
}
</script>

<style scoped>.red-pocket-content { border: 1px solid rgba(var(--v-theme-error), .2); border-radius: 8px; padding: 10px; }.pocket-heading { display: flex; justify-content: space-between; flex-wrap: wrap; gap: 7px; color: rgb(var(--v-theme-error)); }.pocket-status { font-size: .65rem; white-space: nowrap; }.pocket-awards { display: flex; flex-wrap: wrap; gap: 8px 14px; margin-top: 9px; }.pocket-award { display: flex; align-items: center; gap: 6px; }.pocket-award :deep(img) { width: 24px; height: 24px; object-fit: contain; }.pocket-meta { display: flex; flex-wrap: wrap; gap: 12px; color: rgba(var(--v-theme-on-surface), .4); font-size: .65rem; margin-top: 9px; }.pocket-winners { border-top: 1px solid rgba(var(--v-theme-on-surface), .07); margin-top: 10px; padding-top: 5px; }.pocket-winner { display: flex; align-items: center; gap: 6px; flex-wrap: wrap; margin-top: 7px; font-size: .7rem; }.winner-avatar { --event-avatar-size: 20px; }.winner-name { color: rgba(var(--v-theme-on-surface), .55); margin-right: auto; }</style>
