<template>
  <nav class="analytics-contents" :aria-label="t('views.analytics.contents')">
    <div class="contents-heading">
      <v-icon icon="mdi-format-list-bulleted" size="18"/>
      <span>{{ t('views.analytics.contents') }}</span></div>
    <div class="contents-links">
      <a v-for="section in sections" :key="section.id" :href="`#${section.id}`"
         :aria-current="activeId === section.id ? 'location' : undefined" @click.prevent="navigate(section.id)">{{
        section.title }}</a>
    </div>
    <select class="contents-select" :value="activeId" :aria-label="t('views.analytics.contents')"
            @change="navigate(($event.target as HTMLSelectElement).value)">
      <option v-for="section in sections" :key="section.id" :value="section.id">{{ section.title }}</option>
    </select>
  </nav>
</template>

<script setup lang="ts">
import {onMounted, onScopeDispose, ref, watch} from 'vue'
import {useI18n} from 'vue-i18n'
import type {AnalyticsSection} from '@/models/analyticsSection'

const props = defineProps<{ sections: AnalyticsSection[]; scrollOffset?: number }>()
const {t} = useI18n()
const activeId = ref(props.sections[0]?.id ?? '')
let targets: HTMLElement[] = []
let resizeObserver: ResizeObserver | undefined
let frame: number | undefined

function updateActive(): void {
  frame = undefined
  const first = targets[0]
  if (!first) {
    activeId.value = ''
    return
  }
  const offset = Number.parseFloat(getComputedStyle(first).scrollMarginTop) || 0
  let current = first
  for (const target of targets) {
    if (target.getBoundingClientRect().top <= offset + 1) {
      current = target
    }
  }
  if (window.scrollY > 0 && Math.ceil(window.scrollY + window.innerHeight) >= document.documentElement.scrollHeight) {
    current = targets.at(-1)!
  }
  activeId.value = current.id
}

function scheduleUpdate(): void {
  frame ??= requestAnimationFrame(updateActive)
}

function refreshTargets(): void {
  resizeObserver?.disconnect()
  targets = props.sections.map(section => document.getElementById(section.id)).filter((target): target is HTMLElement => Boolean(target))
  for (const target of targets) {
    resizeObserver?.observe(target)
  }
  scheduleUpdate()
}

function navigate(id: string): void {
  const target = targets.find(section => section.id === id)
  if (target) {
    activeId.value = id
    target.scrollIntoView({
      behavior: window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 'instant' : 'smooth',
      block: 'start'
    })
    target.focus({preventScroll: true})
  }
}

watch(() => props.sections.map(section => section.id).join(','), refreshTargets, {flush: 'post'})
watch(() => props.scrollOffset, scheduleUpdate, {flush: 'post'})
onMounted(() => {
  if (typeof ResizeObserver !== 'undefined') {
    resizeObserver = new ResizeObserver(scheduleUpdate)
  }
  refreshTargets()
  window.addEventListener('scroll', scheduleUpdate, {passive: true})
  window.addEventListener('resize', scheduleUpdate)
})
onScopeDispose(() => {
  resizeObserver?.disconnect()
  window.removeEventListener('scroll', scheduleUpdate)
  window.removeEventListener('resize', scheduleUpdate)
  if (frame !== undefined) {
    cancelAnimationFrame(frame)
  }
})
</script>

<style scoped>
.analytics-contents {
  border: 1px solid rgba(var(--v-theme-on-surface), .1);
  border-radius: 14px;
  padding: 16px 10px;
  background: rgb(var(--v-theme-background));
  max-height: calc(100dvh - var(--v-layout-top, 0px) - 36px);
  overflow-y: auto;
}

.contents-heading {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 0 10px 12px;
  color: rgba(var(--v-theme-on-surface), .55);
  font-size: .75rem;
  font-weight: 600;
}

.contents-links {
  display: grid;
  gap: 4px;
}

.contents-links a {
  padding: 9px 10px;
  border-radius: 8px;
  font-size: .82rem;
  line-height: 1.5;
  color: rgba(var(--v-theme-on-surface), .65);
  overflow-wrap: anywhere;
}

.contents-links a:hover {
  background: rgba(var(--v-theme-on-surface), .05);
}

.contents-links a[aria-current] {
  color: rgb(var(--v-theme-primary));
  background: rgba(var(--v-theme-primary), .12);
}

.contents-links a:focus-visible, .contents-select:focus-visible {
  outline: 2px solid rgb(var(--v-theme-primary));
  outline-offset: 2px;
}

.contents-select {
  display: none;
}

@media (max-width: 1279px) {
  .analytics-contents {
    display: flex;
    align-items: center;
    gap: 12px;
    padding: 8px 12px;
    overflow: visible;
    border-radius: 12px;
  }

  .contents-heading {
    flex-shrink: 0;
    padding: 0;
  }

  .contents-links {
    display: none;
  }

  .contents-select {
    display: block;
    flex: 1;
    min-width: 0;
    color: rgb(var(--v-theme-on-surface));
    background: rgb(var(--v-theme-background));
    font-size: .82rem;
    padding: 4px;
    border-radius: 4px;
  }
}
</style>
