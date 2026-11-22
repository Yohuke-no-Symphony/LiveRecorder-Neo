import type { AppLanguage } from './types'

export const DEFAULT_LANGUAGE: AppLanguage = 'zh-CN'
export const LANGUAGE_STORAGE_KEY = 'lr.language'
export const SUPPORTED_LANGUAGES: readonly AppLanguage[] = ['zh-CN', 'en-US', 'ja-JP']
const LANGUAGE_NAMES: Record<AppLanguage, string> = { 'zh-CN': '中文', 'en-US': 'English', 'ja-JP': '日本語' }
export const LANGUAGE_OPTIONS = SUPPORTED_LANGUAGES.map(value => ({ title: LANGUAGE_NAMES[value], value }))

export function isAppLanguage(value: unknown): value is AppLanguage
{
  return typeof value === 'string' && SUPPORTED_LANGUAGES.includes(value as AppLanguage)
}

export function readGuestLanguage(): AppLanguage | null
{
  try
  {
    const stored = globalThis.localStorage?.getItem(LANGUAGE_STORAGE_KEY)
    return isAppLanguage(stored) ? stored : null
  }
  catch
  {
    return null
  }
}

export function writeGuestLanguage(language: AppLanguage | null): void
{
  try
  {
    if (language)
    {
      globalThis.localStorage?.setItem(LANGUAGE_STORAGE_KEY, language)
    }
    else
    {
      globalThis.localStorage?.removeItem(LANGUAGE_STORAGE_KEY)
    }
  }
  catch
  {
  }
}
