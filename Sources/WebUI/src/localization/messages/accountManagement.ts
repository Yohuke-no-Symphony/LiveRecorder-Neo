import type { MessageSchema } from '@/localization/types'

export const accountManagementZh = {
  default: '默认账号', effective: '当前使用', setDefault: '设为默认', clearDefault: '取消默认',
  relogin: '重新登录', reloginTitle: '重新登录 B站账号', reloginIdentity: '请使用 {name}（UID {uid}）登录',
  fallback: '默认账号暂不可用，当前使用 {name}。默认账号恢复后会重新优先使用。',
  ban: '封禁用户', unban: '解封用户', banned: '已封禁',
  banConfirm: '封禁此用户？', unbanConfirm: '解封此用户？',
  banMessage: '封禁 {name} 后，其现有登录将失效，无法继续访问本站。用户数据和已提交任务会保留。',
  unbanMessage: '解封 {name} 后，用户需要重新登录。',
}

export const accountManagementEn: MessageSchema<typeof accountManagementZh> = {
  default: 'Default account', effective: 'Currently in use', setDefault: 'Set as default', clearDefault: 'Clear default',
  relogin: 'Sign in again', reloginTitle: 'Sign in to Bilibili again', reloginIdentity: 'Sign in as {name} (UID {uid})',
  fallback: 'The default account is unavailable. Using {name} until the default account recovers.',
  ban: 'Ban user', unban: 'Unban user', banned: 'Banned',
  banConfirm: 'Ban this user?', unbanConfirm: 'Unban this user?',
  banMessage: 'Banning {name} ends their existing sessions and prevents access. Their data and submitted jobs are retained.',
  unbanMessage: '{name} will need to sign in again after being unbanned.',
}

export const accountManagementJa: MessageSchema<typeof accountManagementZh> = {
  default: '既定のアカウント', effective: '現在使用中', setDefault: '既定に設定', clearDefault: '既定を解除',
  relogin: '再ログイン', reloginTitle: 'Bilibili アカウントに再ログイン', reloginIdentity: '{name}（UID {uid}）でログインしてください',
  fallback: '既定のアカウントが利用できないため、現在は {name} を使用しています。復旧後は既定のアカウントを優先します。',
  ban: 'ユーザーを停止', unban: '停止を解除', banned: '利用停止中',
  banConfirm: 'このユーザーを停止しますか？', unbanConfirm: '利用停止を解除しますか？',
  banMessage: '{name} の既存のログインを無効にし、サイトへのアクセスを停止します。データと送信済みタスクは保持されます。',
  unbanMessage: '解除後、{name} は再ログインする必要があります。',
}
