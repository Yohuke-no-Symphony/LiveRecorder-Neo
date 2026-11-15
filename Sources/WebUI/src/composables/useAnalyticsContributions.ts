import { computed, onScopeDispose, ref, shallowRef, toValue, watch, type MaybeRefOrGetter } from 'vue'
import { useI18n } from 'vue-i18n'
import { AnalyticsContributionsAPI } from '@/apis/AnalyticsContributionsAPI'
import { ApiError } from '@/apis/APIBase'
import type { AnalyticsContributionPage, ContributionFilter, ContributionView } from '@/models/analyticsContributions'
import type { AnalyticsRange } from '@/models/analyticsRange'

const PAGE_SIZE = 50

export function useAnalyticsContributions(
  sessionId: MaybeRefOrGetter<string>,
  kind: MaybeRefOrGetter<ContributionFilter>,
  view: MaybeRefOrGetter<ContributionView>,
  range: MaybeRefOrGetter<AnalyticsRange | undefined>,
  refreshKey: MaybeRefOrGetter<number | undefined>,
  pageSize: MaybeRefOrGetter<number> = PAGE_SIZE,
)
{
  const { t } = useI18n()
  const page = shallowRef<AnalyticsContributionPage>()
  const cursor = ref(0)
  const history = ref<number[]>([])
  const loading = ref(false)
  const error = ref('')
  const hasPrevious = computed(() => history.value.length > 0)
  const hasNext = computed(() => page.value?.nextCursor != null)
  const limit = computed(() =>
  {
    const value = toValue(pageSize)
    return Number.isSafeInteger(value) && value > 0 ? Math.min(200, value) : PAGE_SIZE
  })
  let generation = 0
  let disposed = false
  let controller: AbortController | undefined
  let pendingCursor = 0
  let pendingHistory: number[] = []

  async function load(nextCursor: number, nextHistory: number[]): Promise<void>
  {
    controller?.abort()
    const operation = ++generation
    const id = toValue(sessionId)
    if (disposed || !id)
    {
      loading.value = false
      return
    }
    controller = new AbortController()
    const signal = controller.signal
    const selectedRange = toValue(range)
    const snapshotId = page.value?.snapshotId
    pendingCursor = nextCursor
    pendingHistory = nextHistory
    loading.value = true
    error.value = ''
    try
    {
      const result = await AnalyticsContributionsAPI.get(id, {
        kind: toValue(kind), view: toValue(view), fromMs: selectedRange?.fromMs, toMs: selectedRange?.toMs,
        cursor: nextCursor, snapshotId, limit: limit.value,
      }, signal)
      if (disposed || signal.aborted || operation !== generation)
      {
        return
      }
      if (result.nextCursor != null && (!Number.isSafeInteger(result.nextCursor) || result.nextCursor <= nextCursor))
      {
        throw new Error(t('contributions.loadFailed'))
      }
      page.value = result
      cursor.value = nextCursor
      history.value = nextHistory
    }
    catch (value)
    {
      if (!disposed && !signal.aborted && operation === generation)
      {
        if (value instanceof ApiError && value.status === 409 && snapshotId)
        {
          page.value = undefined
          cursor.value = 0
          history.value = []
          await load(0, [])
          return
        }
        error.value = value instanceof Error ? value.message : t('contributions.loadFailed')
      }
    }
    finally
    {
      if (!disposed && operation === generation)
      {
        loading.value = false
      }
    }
  }

  function refresh(): void
  {
    page.value = undefined
    cursor.value = 0
    history.value = []
    error.value = ''
    void load(0, [])
  }

  function next(): void
  {
    const nextCursor = page.value?.nextCursor
    if (!loading.value && nextCursor != null)
    {
      void load(nextCursor, [...history.value, cursor.value])
    }
  }

  function previous(): void
  {
    const previousCursor = history.value.at(-1)
    if (!loading.value && previousCursor !== undefined)
    {
      void load(previousCursor, history.value.slice(0, -1))
    }
  }

  function retry(): void
  {
    if (!loading.value)
    {
      void load(pendingCursor, pendingHistory)
    }
  }

  function resizePage(): void
  {
    const pageIndex = Math.floor(cursor.value / limit.value)
    void load(pageIndex * limit.value, Array.from({ length: pageIndex }, (_, index) => index * limit.value))
  }

  watch([
    () => toValue(sessionId), () => toValue(kind), () => toValue(view),
    () => toValue(range)?.fromMs, () => toValue(range)?.toMs, () => toValue(refreshKey), limit,
  ], (values, previous) =>
  {
    if (previous.length && values.slice(0, -1).every((value, index) => value === previous[index]))
    {
      resizePage()
      return
    }
    refresh()
  }, { immediate: true })

  onScopeDispose(() =>
  {
    disposed = true
    generation++
    controller?.abort()
  })

  return { page, cursor, loading, error, hasPrevious, hasNext, refresh, next, previous, retry }
}
