<template>
  <span class="medal" :title="level ? `${name} ${formatNumber(level)}` : name"><span class="medal-name">{{ name }}</span><b v-if="level">{{ formatNumber(level) }}</b></span>
</template>

<script setup lang="ts">
import { formatNumber } from '@/utilities/format'

defineProps<{ name: string; level?: number }>()
</script>

<style scoped>
.medal { display: inline-grid; flex: 0 1 auto; min-width: 0; grid-template-columns: minmax(0, 1fr) auto; align-items: center; max-width: 100%; overflow: hidden; border: 1px solid rgba(var(--v-theme-secondary), .25); border-radius: 4px; color: rgb(var(--v-theme-secondary)); background: rgba(var(--v-theme-secondary), .1); font-size: .6rem; line-height: 1.5; vertical-align: middle; }
.medal-name { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; padding: 0 4px; }
.medal b { padding: 0 4px; border-left: 1px solid rgba(var(--v-theme-secondary), .2); background: rgba(var(--v-theme-secondary), .1); font-weight: 650; font-variant-numeric: tabular-nums; }
</style>
