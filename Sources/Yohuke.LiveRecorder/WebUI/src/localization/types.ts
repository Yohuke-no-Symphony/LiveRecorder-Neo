export type AppLanguage = 'zh-CN' | 'en-US' | 'ja-JP'

export type MessageSchema<T> = {
  [K in keyof T]: T[K] extends string ? string : MessageSchema<T[K]>
}
