import { onBeforeUnmount, onMounted } from 'vue'

export function usePolling(callback: () => Promise<unknown>, intervalMs = 10000, immediate = true)
{
  let timer: ReturnType<typeof setTimeout> | undefined
  let stopped = false

  async function poll(): Promise<void>
  {
    try
    {
      await callback()
    }
    finally
    {
      if (!stopped)
      {
        timer = setTimeout(() => { void poll() }, intervalMs)
      }
    }
  }

  onMounted(() =>
  {
    if (immediate)
    {
      void poll()
    }
    else
    {
      timer = setTimeout(() => { void poll() }, intervalMs)
    }
  })
  onBeforeUnmount(() =>
  {
    stopped = true
    clearTimeout(timer)
  })
}
