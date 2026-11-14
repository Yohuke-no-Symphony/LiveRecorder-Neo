import { post } from './APIBase'
import type { AiSettings } from '@/models/settings'

export const AiSettingsAPI = {
  test: (settings: AiSettings) => post<{ success: boolean; promptTokens: number | null; completionTokens: number | null }>('settings/ai/test', settings),
}
