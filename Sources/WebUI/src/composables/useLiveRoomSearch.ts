import { onScopeDispose, ref, watch } from 'vue'
import { AuthAPI } from '@/apis/AuthAPI'
import type { BilibiliRoomSearchIssue, BilibiliRoomSearchResult } from '@/models/bilibili'
import { i18n } from '@/plugins/i18n'

export function useLiveRoomSearch(accountId: () => string | null)
{
  const keyword = ref('')
  const results = ref<BilibiliRoomSearchResult[]>([])
  const issues = ref<BilibiliRoomSearchIssue[]>([])
  const loading = ref(false)
  const loadingMore = ref(false)
  const hasMore = ref(false)
  const searched = ref(false)
  const error = ref('')
  let controller: AbortController | undefined
  let generation = 0
  let page = 0
  let submittedKeyword = ''

  function cancel(): void
  {
    generation++
    controller?.abort()
    controller = undefined
    loading.value = false
    loadingMore.value = false
  }

  function reset(): void
  {
    cancel()
    page = 0
    submittedKeyword = ''
    results.value = []
    issues.value = []
    hasMore.value = false
    searched.value = false
    error.value = ''
  }

  async function fetchPage(append: boolean): Promise<void>
  {
    if (append && (!hasMore.value || loading.value || loadingMore.value))
    {
      return
    }
    if (!append)
    {
      reset()
    }
    const operation = generation
    const request = new AbortController()
    controller = request
    const requestedPage = append ? page + 1 : 1
    const query = append ? submittedKeyword : keyword.value.trim()
    loading.value = !append
    loadingMore.value = append
    error.value = ''
    try
    {
      const response = await AuthAPI.searchBilibiliRooms({ keyword: query, page: requestedPage, accountId: accountId() }, request.signal)
      if (operation !== generation || request.signal.aborted)
      {
        return
      }
      const ids = new Set<string>()
      results.value = [...(append ? results.value : []), ...response.results].filter(room =>
      {
        if (ids.has(room.roomId))
        {
          return false
        }
        ids.add(room.roomId)
        return true
      })
      issues.value = response.issues
      page = requestedPage
      submittedKeyword = query
      hasMore.value = response.hasMore
      searched.value = true
    }
    catch (value)
    {
      if (operation === generation && !request.signal.aborted)
      {
        error.value = value instanceof Error ? value.message : i18n.global.t('components.targets.searchFailed')
      }
    }
    finally
    {
      if (operation === generation)
      {
        controller = undefined
        loading.value = false
        loadingMore.value = false
      }
    }
  }

  watch(accountId, reset, { flush: 'sync' })
  onScopeDispose(cancel)
  return { keyword, results, issues, loading, loadingMore, hasMore, searched, error, search: () => fetchPage(false), loadMore: () => fetchPage(true) }
}
