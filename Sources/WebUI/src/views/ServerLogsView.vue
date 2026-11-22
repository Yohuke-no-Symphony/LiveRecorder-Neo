<template>
  <PageHeader :title="title"/>
  <ContentCard class="server-logs-card">
    <v-alert v-if="resetReason" type="info" density="compact" class="server-logs-notice mb-3" role="status">{{
      t(`serverLogs.${resetReason}`) }}</v-alert>
    <LogEntryList :entries="visibleEntries" :loading="loading" :error="error" :retrying="retrying"
                  :title="title" :stream-key="`${streamKey}:${query}`" :entry-limit="MAX_SERVER_LOG_ENTRIES" :label-level="labelLevel"
                  :empty-text="query ? t('serverLogs.noMatches') : undefined" fill-height>
      <template #search>
        <v-text-field v-model="search" :label="t('serverLogs.search')" class="server-logs-search" type="search"
                      prepend-inner-icon="mdi-magnify" density="compact" clearable hide-details/>
      </template>
      <template #metadata="{ entry }">
        <span class="log-source mono muted">{{ entry.category }}</span>
        <code v-if="entry.exceptionType" class="log-source muted">{{ entry.exceptionType }}</code>
      </template>
    </LogEntryList>
  </ContentCard>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import ContentCard from '@/components/common/ContentCard.vue'
import LogEntryList from '@/components/common/LogEntryList.vue'
import PageHeader from '@/components/common/PageHeader.vue'
import { MAX_SERVER_LOG_ENTRIES, useServerLogs } from '@/composables/useServerLogs'
import { useAuthStore } from '@/stores/auth'

const { t, te } = useI18n()
const auth = useAuthStore()
const title = computed(() => t('shell.nav.serverLogs'))
const { entries, streamKey, resetReason, loading, retrying, error } = useServerLogs(() =>
  auth.isAdmin ? auth.user?.id ?? null : null)
const search = ref<string | null>('')
const query = computed(() => (search.value ?? '').trim().toLowerCase())
const visibleEntries = computed(() =>
{
  const term = query.value
  return term ? entries.value.filter(entry =>
    [entry.message, entry.category, entry.level, labelLevel(entry.level), entry.exceptionType]
      .some(field => field?.toLowerCase().includes(term))) : entries.value
})

function labelLevel(level: string): string
{
  const key = `serverLogs.levels.${level}`
  return te(key) ? t(key) : level
}
</script>

<style scoped>
.server-logs-card {
  display: flex;
  flex: 1 1 auto;
  flex-direction: column;
  min-height: 0;
}

.server-logs-card > :deep(.content-card-body) {
  display: flex;
  flex: 1;
  flex-direction: column;
}

.server-logs-notice {
  flex-shrink: 0;
}

.server-logs-search {
  flex: 0 1 320px;
  min-width: min(100%, 240px);
}

.log-source {
  min-width: 0;
  overflow-wrap: anywhere;
}

@media (max-width: 600px) {
  .server-logs-search {
    flex-basis: 100%;
    min-width: 0;
  }
}
</style>
