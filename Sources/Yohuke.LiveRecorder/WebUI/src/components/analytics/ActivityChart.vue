<template>
  <div class="activity-chart">
    <div class="d-flex justify-space-between align-center mb-7"><h2 class="section-heading mb-0">直播趋势</h2><v-btn-toggle v-model="metric" mandatory density="compact" variant="tonal" color="primary"><v-btn value="messages" size="small">消息</v-btn><v-btn value="paidAmount" size="small">观测金额</v-btn></v-btn-toggle></div>
    <div v-if="!buckets.length" class="empty-state py-10">暂无数据</div>
    <template v-else>
      <div class="chart-content"><span class="chart-max mono">{{ metric === 'paidAmount' ? formatMoney(maximum) : maximum }}</span><svg viewBox="0 0 1000 220" role="img" :aria-label="metric === 'paidAmount' ? '直播观测金额趋势' : '直播消息数量趋势'" preserveAspectRatio="none"><line v-for="y in [20, 80, 140, 200]" :key="y" x1="0" :y1="y" x2="1000" :y2="y" class="chart-grid" /><g v-for="(bucket, index) in buckets" :key="bucket.offsetMs" class="chart-bucket" tabindex="0" role="button" :aria-label="`${formatDuration(bucket.offsetMs)} ${metric === 'paidAmount' ? formatMoney(bucket.paidAmount) : `${bucket.messages} 条`}`" @click="$emit('seek', bucket.offsetMs)" @keydown.enter="$emit('seek', bucket.offsetMs)"><title>{{ formatDuration(bucket.offsetMs) }} · {{ metric === 'paidAmount' ? formatMoney(bucket.paidAmount) : `${bucket.messages} 条消息` }}</title><rect :x="bucket.offsetMs / Math.max(durationMs, 1) * 1000" :y="200 - Math.max(1, bucket[metric] / maximum * 180)" :width="barWidth(index)" :height="Math.max(1, bucket[metric] / maximum * 180)" rx="2" class="chart-bar" /></g></svg></div>
      <div class="d-flex justify-space-between mt-3 mono text-caption muted"><span>00:00</span><span>{{ formatDuration(durationMs / 2) }}</span><span>{{ formatDuration(durationMs) }}</span></div>
    </template>
  </div>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue'
import type { Analytics } from '@/models/media'
import { formatDuration, formatMoney } from '@/utilities/format'
const props = defineProps<{ buckets: Analytics['buckets']; durationMs: number }>()
defineEmits<{ seek: [offsetMs: number] }>()
const metric = ref<'messages' | 'paidAmount'>('messages')
const maximum = computed(() => Math.max(1, ...props.buckets.map(bucket => bucket[metric.value])))
function barWidth(index: number): number
{
  const next = props.buckets[index + 1]?.offsetMs ?? props.durationMs
  return Math.max(1, Math.min(80, (next - props.buckets[index]!.offsetMs) / Math.max(props.durationMs, 1) * 1000 - 2))
}
</script>

<style scoped>.chart-content { position: relative; padding-top: 12px; }.chart-content svg { display: block; width: 100%; height: 220px; overflow: visible; }.chart-grid { stroke: rgba(var(--v-theme-on-surface), .07); stroke-width: 1; }.chart-bar { fill: rgb(var(--v-theme-primary)); opacity: .75; transition: opacity .15s; }.chart-bucket { cursor: pointer; }.chart-bucket:hover .chart-bar, .chart-bucket:focus .chart-bar { opacity: 1; }.chart-max { position: absolute; top: -10px; left: 0; font-size: .65rem; color: rgba(var(--v-theme-on-surface), .4); }</style>
