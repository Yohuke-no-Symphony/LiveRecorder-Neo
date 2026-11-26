import type { AppLanguage } from '@/localization/types'

export type UserPermissions = string[]

export interface PermissionDefinition
{
  id: string
  group: string
  labelKey: string
  descriptionKey: string | null
  icon: string
  isDangerous: boolean
  defaultGranted: boolean
}

export const PERMISSIONS = {
  viewRecordings: 'recordings.view',
  downloadRecordings: 'recordings.download',
  createClips: 'clips.create',
  manageClips: 'clips.manage',
  uploadLiveMarkers: 'live-markers.upload',
  generateLiveMarkers: 'live-markers.generate',
  manageLiveMarkers: 'live-markers.manage',
  manageRecordings: 'recordings.manage',
  configureRecordings: 'recordings.configure',
  viewAnalytics: 'analytics.view',
  generateAnalytics: 'analytics.generate',
  purgeRecordings: 'recordings.purge',
} as const

export interface UserProfile
{
  id: string
  email: string
  displayName: string
  role: 'Admin' | 'User'
  permissions: UserPermissions
  status: 'Active' | 'Pending' | 'Rejected' | 'Disabled'
  avatarUrl?: string | null
  language?: AppLanguage | null
  analyticsRangePinned?: boolean
  playbackEventRetentionCount?: number
}

export interface UserSession
{
  user: UserProfile | null
  csrfToken: string
}

export interface PublicUserProfile
{
  id: string
  displayName: string
  avatarUrl: string
}

export interface PendingEmailChange
{
  email: string
  expiresAtUtc: string
  nextSendAtUtc: string
}

export interface AccountSettings
{
  hasLocalPassword: boolean
  canChangeEmail: boolean
  pendingEmailChange?: PendingEmailChange | null
}

export interface Registration
{
  email: string
  password: string
  displayName: string
  emailCode?: string
  invitationCode?: string
}

export interface RegistrationOptions
{
  enabled: boolean
  approvalRequired: boolean
  invitationRequired: boolean
  emailVerificationRequired: boolean
}

export interface OAuthExchange
{
  user: UserProfile | null
  registrationRequired: boolean
  emailChangeStarted?: boolean
  email: string
  displayName: string
}

export interface Invitation
{
  id: string
  createdAtUtc: string
  expiresAtUtc?: string | null
  maxUses: number
  usedCount: number
  revoked: boolean
  code?: string
}

export interface BilibiliAccount
{
  id: string
  uid: number
  userName: string
  avatarUrl: string
  status: string
  lastValidatedAtUtc?: string | null
  isDefault?: boolean
  isEffectiveDefault?: boolean
}

export interface BilibiliChallenge
{
  type: 'geetest' | 'geetest4' | 'img'
  token: string
  gt: string
  challenge?: string
  imageUrl?: string
}

export interface BilibiliLoginSession
{
  sessionId: string
  status: 'Waiting' | 'Scanned' | 'Expired' | 'Challenge' | 'SmsSent' | 'Complete' | 'VerificationRequired' | 'Failed'
  qrUrl?: string
  challenge?: BilibiliChallenge
  account?: BilibiliAccount
  message?: string
  verificationUrl?: string
}

export interface SmsChallengeAnswer
{
  token: string
  challenge?: string
  validate?: string
  seccode?: string
  lotNumber?: string
  captchaOutput?: string
  passToken?: string
  genTime?: string
  captcha?: string
}
