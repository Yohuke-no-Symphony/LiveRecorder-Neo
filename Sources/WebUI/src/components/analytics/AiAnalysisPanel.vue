<template>
  <ContentCard :title="t('replay.aiTitle')" class="ai-analysis-results">
    <v-alert v-if="resultStale" type="info" variant="tonal" class="my-4">{{ t('replay.aiStale') }}</v-alert>
    <div v-if="hasBothResults" class="analysis-actions my-4">
      <v-btn size="small" :variant="showPartial ? 'text' : 'tonal'" @click="showPartial = false">{{
        t('replay.previousComplete') }}
      </v-btn>
      <v-btn size="small" :variant="showPartial ? 'tonal' : 'text'" @click="showPartial = true">{{
        t('replay.currentPartial') }}
      </v-btn>
    </div>
    <template v-if="result">
      <p v-if="!result.isComplete" class="text-caption muted my-3">{{ t('replay.aiPartial') }}</p>
      <p v-if="result.overview" class="analysis-overview my-4">{{ result.overview }}</p>
      <p v-if="!topics.length" class="text-caption muted py-4">{{ t('replay.topicsEmpty') }}</p>
      <article v-for="topic in topics" :key="topic.id" class="topic-card">
        <div class="topic-heading">
          <h3 class="text-subtitle-1">{{ topic.title }}</h3>
          <a class="mono text-caption" :href="playbackUrl(topic.startMs)" target="_blank" rel="noopener noreferrer"
             :aria-label="t('replay.play')">{{ formatDuration(topic.startMs) }} — {{ formatDuration(topic.endMs) }}
            ↗</a>
        </div>
        <p class="text-body-2 my-3">{{ topic.summary }}</p>
        <p v-if="topic.observations" class="text-caption muted mb-3"><strong>{{ t('replay.observations') }}：</strong>{{
          topic.observations }}</p>
        <div class="analysis-actions">
          <v-btn size="small" variant="text" @click="emit('range', { fromMs: topic.startMs, toMs: topic.endMs })">{{
            t('replay.topicSelect') }}
          </v-btn>
          <a class="text-caption" :href="playbackUrl(topic.startMs)" target="_blank" rel="noopener noreferrer">{{
            t('replay.play') }} ↗</a>
        </div>
        <details v-if="topic.evidence.length" class="topic-evidence mt-3">
          <summary class="text-caption">{{ t('replay.evidence') }} · {{ topic.evidence.length }}</summary>
          <blockquote v-for="evidence in topic.evidence" :key="evidence.id" class="text-body-2 mt-3">
            <a class="mono text-caption" :href="playbackUrl(evidence.offsetMs)" target="_blank"
               rel="noopener noreferrer">{{ formatDuration(evidence.offsetMs) }} ↗</a>
            <p>{{ evidence.text }}</p>
          </blockquote>
        </details>
      </article>
    </template>
    <p v-else-if="!loading" class="text-caption muted py-6">{{ t('replay.aiEmpty') }}</p>
  </ContentCard>
</template>

<script setup lang="ts">
import {computed} from 'vue'
import {useRouter} from 'vue-router'
import {useI18n} from 'vue-i18n'
import type {AiAnalysisResult} from '@/models/aiAnalysis'
import type {AnalyticsRange} from '@/models/analyticsRange'
import {formatDuration} from '@/utilities/format'
import ContentCard from '@/components/common/ContentCard.vue'

const props = defineProps<{
  sessionId: string;
  result?: AiAnalysisResult | null;
  resultStale: boolean;
  hasBothResults: boolean;
  loading: boolean
}>()
const showPartial = defineModel<boolean>('showPartial', {required: true})
const emit = defineEmits<{ range: [value: AnalyticsRange] }>()
const {t} = useI18n()
const router = useRouter()
const topics = computed(() => props.result?.topics ?? [])

function playbackUrl(offsetMs: number): string {
  return router.resolve({path: `/sessions/${props.sessionId}`, query: {at: Math.floor(offsetMs)}}).href
}
</script>

<style scoped>
.analysis-actions, .topic-heading {
  display: flex;
  align-items: center;
  gap: 12px;
  flex-wrap: wrap;
}

.topic-heading {
  justify-content: space-between;
}

.analysis-overview, .topic-card p {
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

.topic-card {
  border-top: 1px solid rgba(var(--v-theme-on-surface), .12);
  padding: 20px 0;
}

.topic-card:last-child {
  padding-bottom: 0;
}

a {
  color: rgb(var(--v-theme-primary));
}

blockquote {
  border-left: 2px solid rgba(var(--v-theme-primary), .35);
  padding-left: 12px;
}

.topic-evidence summary {
  cursor: pointer;
  color: rgba(var(--v-theme-on-surface), .7);
}
</style>
