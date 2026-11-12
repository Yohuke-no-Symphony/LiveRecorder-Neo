import { ref } from 'vue'
import { i18n } from '@/plugins/i18n'

export function useAction()
{
  const busy = ref(false)
  const error = ref('')

  async function run<T>(operation: () => Promise<T>): Promise<T | undefined>
  {
    busy.value = true
    error.value = ''
    try
    {
      return await operation()
    }
    catch (value)
    {
      error.value = value instanceof Error ? value.message : i18n.global.t('components.common.operationFailed')
      return undefined
    }
    finally
    {
      busy.value = false
    }
  }

  return { busy, error, run }
}
