import type { MessageSchema } from '../types'

export const coreZh = {
  brand: { name: '夜半录播', yohuke: '夜半' },
  language: { label: '界面语言', followDefault: '跟随全局默认', saveFailed: '语言设置保存失败' },
  errors: {
    connection: '无法连接服务器', startup: '连接服务器失败', retry: '重试',
    request: '请求失败（{status}）', action: '操作失败，请重试',
    unauthorized: '登录已失效，请重新登录', forbidden: '没有执行此操作的权限',
    tooManyRequests: '请求过于频繁，请稍后重试',
  },
}

export const coreEn: MessageSchema<typeof coreZh> = {
  brand: { name: 'Yohuke Live Recorder', yohuke: 'Yohuke' },
  language: { label: 'Interface language', followDefault: 'Use site default', saveFailed: 'Could not save language preference' },
  errors: {
    connection: 'Cannot connect to the server', startup: 'Server connection failed', retry: 'Retry',
    request: 'Request failed ({status})', action: 'The operation failed. Please try again.',
    unauthorized: 'Your session has expired. Please sign in again.', forbidden: 'You do not have permission to perform this action.',
    tooManyRequests: 'Too many requests. Please try again later.',
  },
}

export const coreJa: MessageSchema<typeof coreZh> = {
  brand: { name: '夜更けのレコーダー', yohuke: '夜更け' },
  language: { label: '表示言語', followDefault: 'サイトの既定言語に従う', saveFailed: '言語設定を保存できませんでした' },
  errors: {
    connection: 'サーバーに接続できません', startup: 'サーバーへの接続に失敗しました', retry: '再試行',
    request: 'リクエストに失敗しました（{status}）', action: '操作に失敗しました。もう一度お試しください。',
    unauthorized: 'セッションが切れました。もう一度ログインしてください。', forbidden: 'この操作を行う権限がありません。',
    tooManyRequests: 'リクエストが多すぎます。しばらくしてからお試しください。',
  },
}
