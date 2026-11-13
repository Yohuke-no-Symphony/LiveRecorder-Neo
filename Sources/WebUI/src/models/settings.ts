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
  defaultLanguage: AppLanguage
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
