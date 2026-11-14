import type { AppLanguage } from '@/localization/types'
import type { UserPermissions } from './auth'

export interface SmtpSettings
{
  host: string
  port: number
  useSsl: boolean
  checkCertificateRevocation: boolean
  userName: string
  password: string
  fromAddress: string
  fromName: string
}

export interface OAuthProviderSettings
{
  id: string
  name: string
  enabled: boolean
  authorizationEndpoint: string
  tokenEndpoint: string
  userInfoEndpoint: string
  clientId: string
  clientSecret: string
  scope: string
  subjectField: string
  nameField: string
  emailField: string
  avatarField: string
  useBasicAuthentication: boolean
}

export interface ServerSettings
{
  publicWebUrl?: string
  defaultBilibiliAccountId?: string | null
  ai?: AiSettings
  defaultLanguage: AppLanguage
  translationDanmuPattern: string
  id: string
  timeZone: string
  quality: number
  recordDanmu: boolean
  segmentBytes: number
  recordingRetentionDays: number
  clipRetentionDays: number
  trashRetentionDays: number
  cacheLimitBytes: number
  cacheRetentionMinutes: number
  minimumFreeBytes: number
  mediaConcurrency: number
  playbackConcurrency: number
  monitorIntervalSeconds: number
  sessionGraceMinutes: number
  streamDisconnectTimeoutMinutes: number
  registrationEnabled: boolean
  defaultUserPermissions: UserPermissions
  registrationApprovalRequired: boolean
  registrationInvitationRequired: boolean
  registrationEmailVerificationRequired: boolean
  ffmpegPath: string
  ffprobePath: string
  smtp: SmtpSettings
  oauthProviders: OAuthProviderSettings[]
}

export interface AiSettings
{
  enabled: boolean
  provider: 'OpenAI' | 'DeepSeek' | 'Custom'
  baseUrl: string
  model: string
  apiKey: string
  hasApiKey: boolean
  clearApiKey: boolean
  tokensPerHour: number
  minimumTokens: number
  maximumTokens: number
  maxOutputTokens: number
  contextTokenTarget: number
  contextWindowTokens: number
  requestTimeoutSeconds: number
  maxRequestRetries: number
  maxModelRequests: number
  maxToolCalls: number
  thinkingMode: 'Default' | 'Enabled' | 'Disabled'
  reasoningEffort: 'Default' | 'Low' | 'High' | 'Max'
}

export function defaultAiSettings(): AiSettings
{
  return { enabled: false, provider: 'OpenAI', baseUrl: 'https://api.openai.com/v1', model: '', apiKey: '', hasApiKey: false, clearApiKey: false, tokensPerHour: 20000, minimumTokens: 10000, maximumTokens: 1000000, maxOutputTokens: 2048, contextTokenTarget: 32000, contextWindowTokens: 0, requestTimeoutSeconds: 180, maxRequestRetries: 2, maxModelRequests: 0, maxToolCalls: 0, thinkingMode: 'Default', reasoningEffort: 'Default' }
}

export function normalizeAiSettings(value?: Partial<AiSettings> | null): AiSettings
{
  return {
    ...defaultAiSettings(), ...value, contextWindowTokens: value?.contextWindowTokens ?? 0,
    requestTimeoutSeconds: value?.requestTimeoutSeconds ?? 180, maxRequestRetries: value?.maxRequestRetries ?? 2,
  }
}

export function isValidAiContextWindow(settings: Pick<AiSettings, 'contextWindowTokens' | 'maxOutputTokens'>): boolean
{
  return Number.isInteger(settings.contextWindowTokens) && (settings.contextWindowTokens === 0 ||
    settings.contextWindowTokens >= 8192 && settings.contextWindowTokens > settings.maxOutputTokens)
}

export function isValidAiRequestLimit(value: number): boolean
{
  return Number.isInteger(value) && value >= 0 && value <= 2147483647
}

export interface ServerPaths
{
  dataDirectory: string
  recordsDirectory: string
  cacheDirectory: string
  configFile: string
  configuredDataDirectory: string
  configuredRecordsDirectory: string
  restartRequired: boolean
}

export const QUALITY_OPTIONS = [
  { titleKey: 'components.quality.original', value: 10000 },
  { titleKey: 'components.quality.bluRay', value: 400 },
  { titleKey: 'components.quality.ultra', value: 250 },
  { titleKey: 'components.quality.high', value: 150 },
  { titleKey: 'components.quality.smooth', value: 80 },
] as const
