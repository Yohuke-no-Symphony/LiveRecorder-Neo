export function formatBytes(bytes: number): string
{
  if (!Number.isFinite(bytes) || bytes < 0)
  {
    return '—'
  }
  const units = ['B', 'KB', 'MB', 'GB', 'TB', 'PB']
  const index = bytes > 0 ? Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), units.length - 1) : 0
  return `${new Intl.NumberFormat('zh-CN', { maximumFractionDigits: index > 0 ? 1 : 0 }).format(bytes / 1024 ** index)} ${units[index]}`
}

export function formatDuration(milliseconds: number, includeHours = false): string
{
  const totalSeconds = Math.max(0, Math.floor(milliseconds / 1000))
  const seconds = String(totalSeconds % 60).padStart(2, '0')
  const minutes = String(Math.floor(totalSeconds / 60) % 60).padStart(2, '0')
  const hours = Math.floor(totalSeconds / 3600)
  return hours > 0 || includeHours ? `${String(hours).padStart(2, '0')}:${minutes}:${seconds}` : `${minutes}:${seconds}`
}

export function formatDate(value?: string | null): string
{
  if (!value)
  {
    return '—'
  }
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? '—' : new Intl.DateTimeFormat('zh-CN', {
    year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', hour12: false,
  }).format(date)
}

export function formatMoney(value: number): string
{
  return new Intl.NumberFormat('zh-CN', { style: 'currency', currency: 'CNY', minimumFractionDigits: 2, maximumFractionDigits: 3 }).format(value)
}

export function safeReturnPath(value: unknown): string
{
  return typeof value === 'string' && value.startsWith('/') && !value.startsWith('//') && !value.includes('\\') && !value.startsWith('/oauth') ? value : '/'
}
