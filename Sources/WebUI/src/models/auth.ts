import type { AppLanguage } from '@/localization/types'

export interface UserPermissions
{
  manageRecordings: boolean
  viewAnalytics: boolean
  purgeRecordings: boolean
}

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
}

export interface UserSession
{
  user: UserProfile | null
  csrfToken: string
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
