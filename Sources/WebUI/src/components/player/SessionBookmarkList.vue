<template>
  <v-card class="session-bookmarks" variant="flat">
    <CardHeader :title="t('components.bookmarks.title')">
      <template #suffix><span v-if="bookmarks.length" class="bookmark-count mono">{{ formatNumber(bookmarks.length) }}</span></template>
      <template #actions><v-btn icon="mdi-refresh" variant="text" size="small" :loading="loading" :disabled="saving" :aria-label="t('components.bookmarks.refresh')" @click="emit('refresh')" /></template>
    </CardHeader>
    <v-progress-linear v-if="loading" indeterminate color="primary" :aria-label="t('components.bookmarks.loading')" />
    <v-alert v-if="error" type="error" variant="tonal" density="compact" class="ma-4">{{ error }}</v-alert>
    <ul v-if="bookmarks.length" class="bookmark-rows">
      <li v-for="bookmark in bookmarks" :key="bookmark.id" class="bookmark-row">
        <button type="button" class="bookmark-target" :aria-label="t('components.bookmarks.jump', { title: bookmark.title, time: formatTimecode(bookmark.offsetMs) })" @click="emit('seek', bookmark.offsetMs)">
          <v-icon icon="mdi-bookmark-outline" size="19" class="bookmark-symbol" />
          <span class="bookmark-content"><span class="bookmark-title">{{ bookmark.title }}</span><span class="bookmark-time mono">{{ formatTimecode(bookmark.offsetMs) }}</span></span>
        </button>
        <div class="bookmark-row-actions">
          <v-btn variant="text" size="small" :aria-label="t('components.bookmarks.applyStart') + ' · ' + bookmark.title" @click="emit('start', bookmark.offsetMs)">{{ t('components.bookmarks.applyStart') }}</v-btn>
          <v-btn variant="text" size="small" :aria-label="t('components.bookmarks.applyEnd') + ' · ' + bookmark.title" @click="emit('end', bookmark.offsetMs)">{{ t('components.bookmarks.applyEnd') }}</v-btn>
          <v-menu>
            <template #activator="{ props: menuProps }"><v-btn v-bind="menuProps" icon="mdi-dots-horizontal" variant="text" size="small" :disabled="saving || !editable" :aria-label="t('components.bookmarks.actions', { title: bookmark.title })" /></template>
            <v-list density="compact">
              <v-list-item prepend-icon="mdi-pencil-outline" :title="t('components.bookmarks.edit')" :disabled="saving || !editable" @click="emit('edit', bookmark)" />
              <v-list-item prepend-icon="mdi-delete-outline" :title="t('components.bookmarks.delete')" :disabled="saving || !editable" base-color="error" @click="emit('delete', bookmark)" />
            </v-list>
          </v-menu>
        </div>
      </li>
    </ul>
    <p v-else-if="!loading" class="bookmark-empty text-body-2 muted">{{ t('components.bookmarks.empty') }}</p>
  </v-card>
</template>

<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import type { SessionBookmark } from '@/models/sessionBookmark'
import { formatNumber, formatTimecode } from '@/utilities/format'
import CardHeader from '@/components/common/CardHeader.vue'

withDefaults(defineProps<{ bookmarks: SessionBookmark[]; loading: boolean; saving: boolean; error: string; editable?: boolean }>(), { editable: true })
const emit = defineEmits<{ seek: [offsetMs: number]; start: [offsetMs: number]; end: [offsetMs: number]; edit: [bookmark: SessionBookmark]; delete: [bookmark: SessionBookmark]; refresh: [] }>()
const { t } = useI18n()
</script>

<style scoped>
.bookmark-count { color: rgba(var(--v-theme-on-surface), .45); font-size: .72rem; }
.bookmark-rows { list-style: none; margin: 0; padding: 0 16px 4px; max-height: 365px; overflow-y: auto; }
.bookmark-row { display: flex; align-items: center; gap: 8px; min-height: 64px; border-top: 1px solid rgba(var(--v-theme-on-surface), .07); }
.bookmark-target { display: flex; flex: 1; align-items: center; gap: 12px; min-width: 0; padding: 10px 2px; border: 0; border-radius: 5px; background: none; text-align: left; color: inherit; cursor: pointer; }
.bookmark-target:hover .bookmark-title, .bookmark-target:focus-visible .bookmark-title { color: rgb(var(--v-theme-primary)); }
.bookmark-target:focus-visible { outline: 2px solid rgb(var(--v-theme-primary)); outline-offset: 2px; }
.bookmark-symbol { flex-shrink: 0; color: rgb(var(--v-theme-primary)); }
.bookmark-content { display: flex; flex-direction: column; gap: 3px; min-width: 0; }
.bookmark-title { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-size: .86rem; }
.bookmark-time { color: rgba(var(--v-theme-on-surface), .5); font-size: .7rem; }
.bookmark-row-actions { display: flex; flex-shrink: 0; align-items: center; gap: 2px; }
.bookmark-empty { margin: 0; padding: 0 16px 12px; }
@media (max-width: 650px) { .bookmark-row { flex-wrap: wrap; gap: 0; padding: 6px 0; }.bookmark-target { flex-basis: 100%; }.bookmark-row-actions { margin-left: 26px; } }
</style>
