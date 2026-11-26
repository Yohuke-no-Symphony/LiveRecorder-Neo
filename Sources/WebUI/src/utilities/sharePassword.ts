const STORAGE_KEY = 'lr.pendingSharePassword'
const LIFETIME_MS = 10 * 60 * 1000

interface PendingSharePassword
{
  path: string
  password: string
  expiresAt: number
}

let inMemory: PendingSharePassword | undefined

export function keepSharePassword(path: string, password: unknown): void
{
  if (typeof password !== 'string' || password.length === 0 || password.length > 64)
  {
    return
  }
  inMemory = { path, password, expiresAt: Date.now() + LIFETIME_MS }
  try
  {
    sessionStorage.setItem(STORAGE_KEY, JSON.stringify(inMemory))
  }
  catch
  {
    // In-memory state covers navigation when session storage is unavailable.
  }
}

export function takeSharePassword(path: string): string | undefined
{
  const saved = inMemory ?? readStoredPassword()
  if (!saved)
  {
    return
  }
  const expired = saved.expiresAt <= Date.now()
  if (expired || saved.path === path)
  {
    inMemory = undefined
    try
    {
      sessionStorage.removeItem(STORAGE_KEY)
    }
    catch
    {
      // The in-memory value was already cleared.
    }
  }
  return !expired && saved.path === path ? saved.password : undefined
}

function readStoredPassword(): PendingSharePassword | undefined
{
  try
  {
    const value = JSON.parse(sessionStorage.getItem(STORAGE_KEY) ?? 'null') as Partial<PendingSharePassword> | null
    if (value && typeof value.path === 'string' && typeof value.password === 'string'
      && typeof value.expiresAt === 'number' && Number.isFinite(value.expiresAt))
    {
      return { path: value.path, password: value.password, expiresAt: value.expiresAt }
    }
  }
  catch
  {
    return
  }
}
