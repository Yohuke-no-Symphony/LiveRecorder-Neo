import { onScopeDispose, ref, watch } from 'vue'
import { MediaAPI } from '@/apis/MediaAPI'
import type { BroadcastSession } from '@/models/media'
import { i18n } from '@/plugins/i18n'
import { usePolling } from './usePolling'

const PAGE_SIZE = 50

export function useSessionPages(parameters: () => { streamerId?: string; search?: string })
{
  const sessions = ref<BroadcastSession[]>([])
  const loading = ref(false)
  const loadingMore = ref(false)
  const hasMore = ref(false)
  const error = ref('')
  let cursor: string | undefined
  let generation = 0
  let refreshing = false

  function merge(first: BroadcastSession[], second: BroadcastSession[]): BroadcastSession[]
  {
    const ids = new Set<string>()
    return [...first, ...second].filter(session =>
    {
      if (ids.has(session.id)) { return false }
      ids.add(session.id)
      return true
    })
  }

  async function fetchPage(before?: string): Promise<BroadcastSession[]>
  {
    const { streamerId, search } = parameters()
    return await MediaAPI.sessions(streamerId, search || undefined, before, PAGE_SIZE)
  }

  async function reload(): Promise<void>
  {
    const operation = ++generation
    sessions.value = []
    cursor = undefined
    hasMore.value = false
    loadingMore.value = false
    loading.value = true
    error.value = ''
    try
    {
      const page = await fetchPage()
      if (operation !== generation) { return }
      sessions.value = merge(page, [])
      cursor = page.at(-1)?.id
      hasMore.value = page.length === PAGE_SIZE
    }
    catch (value)
    {
      if (operation === generation) { error.value = value instanceof Error ? value.message : i18n.global.t('components.library.loadFailed') }
    }
    finally
    {
      if (operation === generation) { loading.value = false }
    }
  }

  async function loadMore(): Promise<void>
  {
    if (!hasMore.value || loading.value || loadingMore.value || refreshing) { return }
    const operation = generation
    loadingMore.value = true
    error.value = ''
    try
    {
      const page = await fetchPage(cursor)
      if (operation !== generation) { return }
      sessions.value = merge(sessions.value, page)
      cursor = page.at(-1)?.id ?? cursor
      hasMore.value = page.length === PAGE_SIZE
    }
    catch (value)
    {
      if (operation === generation) { error.value = value instanceof Error ? value.message : i18n.global.t('components.library.loadFailed') }
    }
    finally
    {
      if (operation === generation) { loadingMore.value = false }
    }
  }

  async function refresh(): Promise<void>
  {
    if (loading.value || loadingMore.value || refreshing) { return }
    if (!sessions.value.length) { await reload(); return }
    const operation = generation
    const existingIds = new Set(sessions.value.map(session => session.id))
    const collected: BroadcastSession[] = []
    let before: string | undefined
    refreshing = true
    try
    {
      while (true)
      {
        const page = await fetchPage(before)
        if (operation !== generation) { return }
        collected.push(...page)
        if (page.length < PAGE_SIZE)
        {
          sessions.value = merge(collected, [])
          cursor = page.at(-1)?.id ?? collected.at(-1)?.id
          hasMore.value = false
          break
        }
        if (page.some(session => existingIds.has(session.id)))
        {
          sessions.value = merge(collected, sessions.value)
          break
        }
        const next = page.at(-1)?.id
        if (!next || next === before) { throw new Error(i18n.global.t('components.library.invalidPage')) }
        before = next
      }
      error.value = ''
    }
    catch (value)
    {
      if (operation === generation) { error.value = value instanceof Error ? value.message : i18n.global.t('components.library.refreshFailed') }
    }
    finally
    {
      refreshing = false
    }
  }

  watch(() => [parameters().streamerId, parameters().search], () => { void reload() }, { immediate: true })
  usePolling(refresh, 15000, false)
  onScopeDispose(() => { generation++ })
  return { sessions, loading, loadingMore, hasMore, error, reload, loadMore, refresh }
}
