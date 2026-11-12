import { ref } from 'vue'

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
      error.value = value instanceof Error ? value.message : '操作失败'
      return undefined
    }
    finally
    {
      busy.value = false
    }
  }

  return { busy, error, run }
}
