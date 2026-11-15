import { post, put, query, remove, request } from './APIBase'
import { getServerUrl } from './ServerURL'
import type { BilibiliRoomSearchPage } from '@/models/bilibili'
import type { AppLanguage } from '@/localization/types'
import type { SetupRequest, SetupStatus } from '@/models/setup'
import type { BilibiliAccount, BilibiliLoginSession, Invitation, OAuthExchange, Registration, RegistrationOptions, SmsChallengeAnswer, PermissionDefinition, UserPermissions, UserProfile, UserSession } from '@/models/auth'

export const AuthAPI = {
  setupStatus: () => request<SetupStatus>('setup/status'),
  setup: (body: SetupRequest) => post<void>('setup', body),
  permissions: () => request<PermissionDefinition[]>('auth/permissions'),
  me: () => request<UserSession>('auth/me'),
  setLanguage: (language: AppLanguage | null) => put<UserProfile>('auth/language', { language }),
  setAnalyticsRangePinned: (rangePinned: boolean) => put<UserProfile>('auth/analytics-preferences', { rangePinned }),
  login: (email: string, password: string) => post<UserSession>('auth/login', { email, password }),
  logout: () => post<void>('auth/logout'),
  passwordResetOptions: () => request<{ enabled: boolean }>('auth/password-reset/options'),
  requestPasswordReset: (email: string) => post<void>('auth/password-reset/request', { email }),
  completePasswordReset: (token: string, newPassword: string) => post<void>('auth/password-reset/complete', { token, newPassword }),
  emailCode: (email: string) => post<void>('auth/email-code', { email, purpose: 'register' }),
  registrationOptions: () => request<RegistrationOptions>('auth/registration-options'),
  register: (body: Registration) => post<UserProfile>('auth/register', body),
  providers: () => request<{ id: string; name: string }[]>('auth/oauth/providers'),
  identities: () => request<string[]>('auth/identities'),
  oauthBegin: (id: string) => post<{ authorizationUrl: string }>(`auth/oauth/${encodeURIComponent(id)}/begin`),
  oauthBind: (id: string) => post<{ authorizationUrl: string; expiresAtUtc: string }>(`auth/oauth/${encodeURIComponent(id)}/bind`),
  oauthExchange: (id: string, code: string, state: string) => post<OAuthExchange>(`auth/oauth/${encodeURIComponent(id)}/exchange`, { code, state }),
  oauthRegister: (body: Omit<Registration, 'password'>) => post<UserProfile>('auth/oauth/register', body),
  users: () => request<UserProfile[]>('admin/users'),
  approveUser: (id: string, approved: boolean) => post<UserProfile>(`admin/users/${encodeURIComponent(id)}/approval`, { approved }),
  userRole: (id: string, role: 'Admin' | 'User') => post<UserProfile>(`admin/users/${encodeURIComponent(id)}/role`, { role }),
  userPermissions: (id: string, permissions: UserPermissions) => put<UserProfile>(`admin/users/${encodeURIComponent(id)}/permissions`, permissions),
  banUser: (id: string) => post<UserProfile>(`admin/users/${encodeURIComponent(id)}/ban`),
  unbanUser: (id: string) => post<UserProfile>(`admin/users/${encodeURIComponent(id)}/unban`),
  invitations: () => request<Invitation[]>('admin/invitations'),
  createInvitation: (maxUses: number, expiresAtUtc: string | null) => post<Invitation>('admin/invitations', { maxUses, expiresAtUtc }),
  revokeInvitation: (id: string) => remove(`admin/invitations/${encodeURIComponent(id)}`),
  searchBilibiliRooms: (parameters: { keyword: string; page: number; accountId: string | null }, signal?: AbortSignal) => request<BilibiliRoomSearchPage>(`admin/bilibili/rooms/search${query(parameters)}`, { signal }),
  bilibiliAccounts: () => request<BilibiliAccount[]>('admin/bilibili/accounts'),
  setDefaultBilibiliAccount: (accountId: string | null) => put<BilibiliAccount[]>('admin/bilibili/accounts/default', { accountId }),
  bilibiliAccountAvatar: (id: string, version: string) => getServerUrl(`/api/admin/bilibili/accounts/${encodeURIComponent(id)}/avatar?v=${encodeURIComponent(version)}`),
  deleteBilibiliAccount: (id: string) => remove(`admin/bilibili/accounts/${encodeURIComponent(id)}`),
  createQrLogin: (accountId?: string) => post<BilibiliLoginSession>(accountId ? `admin/bilibili/accounts/${encodeURIComponent(accountId)}/qr` : 'admin/bilibili/qr'),
  pollQrLogin: (id: string) => request<BilibiliLoginSession>(`admin/bilibili/qr/${encodeURIComponent(id)}`),
  createSmsLogin: (cid: string, tel: string, accountId?: string) => post<BilibiliLoginSession>(accountId ? `admin/bilibili/accounts/${encodeURIComponent(accountId)}/sms` : 'admin/bilibili/sms', { cid, tel }),
  sendSms: (id: string, body: SmsChallengeAnswer) => post<BilibiliLoginSession>(`admin/bilibili/sms/${encodeURIComponent(id)}/send`, body),
  completeSms: (id: string, code: string) => post<BilibiliLoginSession>(`admin/bilibili/sms/${encodeURIComponent(id)}/complete`, { code }),
}
