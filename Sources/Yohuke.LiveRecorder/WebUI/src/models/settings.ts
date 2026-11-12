export interface SmtpSettings
{
  host: string
  port: number
  useSsl: boolean
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
  id: string
  storagePath: string
  timeZone: string
  quality: number
  recordDanmu: boolean
  segmentBytes: number
  clipRetentionDays: number
  trashRetentionDays: number
  cacheLimitBytes: number
  cacheRetentionMinutes: number
  minimumFreeBytes: number
  mediaConcurrency: number
  playbackConcurrency: number
  monitorIntervalSeconds: number
  sessionGraceMinutes: number
  registrationEnabled: boolean
  ffmpegPath: string
  ffprobePath: string
  smtp: SmtpSettings
  oauthProviders: OAuthProviderSettings[]
}

export const QUALITY_OPTIONS = [
  { title: '原画', value: 10000 },
  { title: '蓝光', value: 400 },
  { title: '超清', value: 250 },
  { title: '高清', value: 150 },
  { title: '流畅', value: 80 },
]
