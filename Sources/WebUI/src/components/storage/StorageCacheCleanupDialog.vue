<template>
  <v-dialog :model-value="opened" max-width="600" scrollable :persistent="busy" :aria-labelledby="titleId" @update:model-value="updateOpen">
    <ContentCard :title="t('views.storage.cleanup.title')" :title-id="titleId">
      <div v-if="result" class="form-stack" role="status" aria-live="polite">
        <v-alert :type="result.failedEntries ? 'warning' : 'success'">
          {{ t('views.storage.cleanup.completed', { files: formatNumber(result.deletedFiles), bytes: formatBytes(result.deletedBytes) }) }}
        </v-alert>
        <dl class="cleanup-results">
          <div v-for="category in result.categories" :key="category.category" class="cleanup-result-row">
            <dt>{{ t(`views.storage.cleanup.categoryLabels.${category.category}`) }}</dt>
            <dd class="muted">{{ t('views.storage.cleanup.categoryResult', { files: formatNumber(category.deletedFiles), bytes: formatBytes(category.deletedBytes) }) }}</dd>
          </div>
        </dl>
        <v-alert v-if="result.skippedEntries" type="info">{{ t('views.storage.cleanup.skipped', { count: formatNumber(result.skippedEntries) }) }}</v-alert>
        <v-alert v-if="result.failedEntries" type="warning">{{ t('views.storage.cleanup.failed', { count: formatNumber(result.failedEntries) }) }}</v-alert>
      </div>
      <form v-else :id="formId" class="form-stack" @submit.prevent="cleanup">
        <fieldset class="cache-category-options" :disabled="busy">
          <legend class="section-heading">{{ t('views.storage.cleanup.categories') }}</legend>
          <v-checkbox v-for="category in categories" :key="category" v-model="selectedCategories" :value="category" :label="t(`views.storage.cleanup.categoryLabels.${category}`)" :disabled="busy" density="compact" />
        </fieldset>
        <v-select v-model="minimumUnusedMinutes" :label="t('views.storage.cleanup.minimumUnusedMinutes')" :items="timeOptions" :disabled="busy" />
        <p class="text-body-2 muted">{{ t('views.storage.cleanup.scope') }}</p>
        <v-alert v-if="error" type="error">{{ error }}</v-alert>
      </form>
      <template #actions>
        <v-btn v-if="result" variant="text" @click="updateOpen(false)">{{ t('views.common.done') }}</v-btn>
        <template v-else>
          <v-btn variant="text" :disabled="busy" @click="updateOpen(false)">{{ t('views.common.cancel') }}</v-btn>
          <v-btn type="submit" :form="formId" color="error" :loading="busy" :disabled="busy || !selectedCategories.length">{{ t('views.storage.cleanup.submit') }}</v-btn>
        </template>
      </template>
    </ContentCard>
  </v-dialog>
</template>

<script setup lang="ts">
import { computed, ref, useId, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { MediaAPI } from '@/apis/MediaAPI'
import { useAction } from '@/composables/useAction'
import type { CacheCleanupCategory, CacheCleanupResult } from '@/models/storage'
import { formatBytes, formatNumber } from '@/utilities/format'
import ContentCard from '@/components/common/ContentCard.vue'

const opened = defineModel<boolean>({ default: false })
const emit = defineEmits<{ cleaned: [result: CacheCleanupResult]; busy: [value: boolean] }>()
const { t } = useI18n()
const formId = useId()
const titleId = useId()
const categories: CacheCleanupCategory[] = ['playback', 'artwork', 'analyticsContributions']
const selectedCategories = ref<CacheCleanupCategory[]>([...categories])
const minimumUnusedMinutes = ref(0)
const result = ref<CacheCleanupResult>()
const { busy, error, run } = useAction()
const timeOptions = computed(() => [
  { title: t('views.storage.cleanup.timeOptions.all'), value: 0 },
  { title: t('views.storage.cleanup.timeOptions.hour'), value: 60 },
  { title: t('views.storage.cleanup.timeOptions.day'), value: 1440 },
  { title: t('views.storage.cleanup.timeOptions.week'), value: 10080 },
  { title: t('views.storage.cleanup.timeOptions.month'), value: 43200 },
])

watch(busy, value => { emit('busy', value) }, { flush: 'sync' })
watch(opened, value =>
{
  if (value && !busy.value)
  {
    selectedCategories.value = [...categories]
    minimumUnusedMinutes.value = 0
    result.value = undefined
    error.value = ''
  }
})

function updateOpen(value: boolean): void
{
  if (!busy.value)
  {
    opened.value = value
  }
}

async function cleanup(): Promise<void>
{
  if (busy.value || result.value || !selectedCategories.value.length)
  {
    return
  }
  const cleaned = await run(() => MediaAPI.cleanupCache({
    categories: [...selectedCategories.value],
    minimumUnusedMinutes: minimumUnusedMinutes.value,
  }))
  if (cleaned)
  {
    result.value = cleaned
    emit('cleaned', cleaned)
  }
}
</script>

<style scoped>
.cache-category-options { min-width: 0; margin: 0; padding: 0; border: 0; }
.cache-category-options legend { padding: 0; }
.cache-category-options :deep(.v-label) { white-space: normal; }
.cleanup-results { display: flex; flex-direction: column; gap: 12px; margin: 0; }
.cleanup-result-row { min-width: 0; overflow-wrap: anywhere; }
.cleanup-result-row dt { font-weight: 600; }
.cleanup-result-row dd { margin: 4px 0 0; }
</style>
