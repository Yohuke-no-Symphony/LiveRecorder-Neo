import type { MessageSchema } from '@/localization/types'

export const shellZh = {
  nav: {
    management: '管理', library: '媒体库', clips: '我的切片', jobs: '后台任务', analytics: '直播分析', trash: '回收站',
    targets: '录制管理', accounts: 'B站账号', users: '用户与邀请', settings: '设置', account: '账户设置',
    playRecording: '播放录播', streamerRecordings: '主播录播', toggleTheme: '切换主题', userMenu: '用户菜单', logout: '退出登录', logoutFailed: '退出失败',
  },
  account: {
    title: '账户设置', language: '界面语言', languageDescription: '语言偏好会保存在账户中，并在其他设备登录时生效。',
    loginMethods: '登录方式', bound: '已绑定', bind: '绑定', noProviders: '暂无外部登录方式', languageSaved: '语言设置已保存',
  },
  settings: {
    title: '设置', save: '保存设置', general: '常规', interface: '界面设置',
    defaultLanguageDescription: '全局默认语言适用于未选择个人语言的用户和首次访问网站的访客。',
    recording: '录制', media: '媒体与存储', registration: '注册', mail: '邮件', oauth: 'OAuth 登录', recordingSettings: '录制设置',
    quality: '清晰度', recordDanmu: '录制弹幕', segmentSize: '文件分段大小（GiB）', monitorInterval: '开播检测间隔（秒）', sessionGrace: '同场重连等待（分钟）',
    filesAndTools: '文件与工具', timeZone: '文件命名时区', ffmpeg: 'FFmpeg', ffprobe: 'FFprobe',
    retention: '保留时间', clipRetention: '切片保留（天）', trashRetention: '回收站保留（天）', cacheRetention: '播放缓存保留（分钟）',
    resourceLimits: '资源限制', cacheLimit: '播放缓存上限（GiB）', minimumFree: '最小空闲空间（GiB）', mediaConcurrency: '后台媒体任务并发', playbackConcurrency: '播放窗口生成并发',
    registrationSettings: '注册设置', registrationEnabled: '开放注册', manageUsers: '管理用户与邀请码',
    registrationApprovalRequired: '使用管理员批准', registrationApprovalDescription: '关闭后，新注册用户自动批准；已有账号状态保持不变。',
    registrationInvitationRequired: '使用邀请码', registrationInvitationDescription: '关闭后，普通注册与 OAuth 首次注册均无需邀请码。',
    registrationEmailVerificationRequired: '使用邮箱验证码', registrationEmailVerificationDescription: '关闭后，普通注册与 OAuth 首次注册均无需邮箱验证码。',
    smtp: 'SMTP', host: '服务器', port: '端口', ssl: 'SSL', userName: '用户名', password: '密码（留空保留）', fromAddress: '发件邮箱', fromName: '发件人',
    checkCertificateRevocation: '检查证书吊销状态',
    certificateRevocationDescription: '默认开启。仅在证书吊销服务不可达时关闭；关闭后仍检查证书的信任链、域名和有效期，但无法确认它是否已被吊销。',
    addProvider: '添加提供方', provider: 'OAuth 提供方', enabled: '启用', deleteProvider: '删除提供方', id: '标识', name: '名称',
    authorizationEndpoint: '授权端点', tokenEndpoint: 'Token 端点', userInfoEndpoint: 'UserInfo 端点', callback: '回调地址',
    clientId: 'Client ID', clientSecret: 'Client Secret（留空保留）', scope: 'Scope', subjectField: '用户 ID 字段', nameField: '名称字段', emailField: '邮箱字段', avatarField: '头像字段',
    basicAuthentication: 'HTTP Basic 客户端认证', noProviders: '暂无 OAuth 提供方', saved: '设置已保存', invalidSegmentSize: '文件分段大小必须大于零', invalidProvider: '请填写 OAuth 提供方标识和名称',
  },
}

export const shellEn: MessageSchema<typeof shellZh> = {
  nav: {
    management: 'Management', library: 'Library', clips: 'My clips', jobs: 'Background jobs', analytics: 'Stream analytics', trash: 'Trash',
    targets: 'Recording management', accounts: 'Bilibili accounts', users: 'Users and invitations', settings: 'Settings', account: 'Account settings',
    playRecording: 'Play recording', streamerRecordings: 'Streamer recordings', toggleTheme: 'Switch theme', userMenu: 'User menu', logout: 'Sign out', logoutFailed: 'Unable to sign out',
  },
  account: {
    title: 'Account settings', language: 'Interface language', languageDescription: 'Your language preference is saved to your account and applies when you sign in on other devices.',
    loginMethods: 'Sign-in methods', bound: 'Linked', bind: 'Link', noProviders: 'No external sign-in methods available', languageSaved: 'Language preference saved',
  },
  settings: {
    title: 'Settings', save: 'Save settings', general: 'General', interface: 'Interface settings',
    defaultLanguageDescription: 'The global default applies to users without a personal language preference and visitors opening the website for the first time.',
    recording: 'Recording', media: 'Media and storage', registration: 'Registration', mail: 'Email', oauth: 'OAuth sign-in', recordingSettings: 'Recording settings',
    quality: 'Quality', recordDanmu: 'Record chat', segmentSize: 'File segment size (GiB)', monitorInterval: 'Live status check interval (seconds)', sessionGrace: 'Session reconnection window (minutes)',
    filesAndTools: 'Files and tools', timeZone: 'File naming time zone', ffmpeg: 'FFmpeg', ffprobe: 'FFprobe',
    retention: 'Retention', clipRetention: 'Clip retention (days)', trashRetention: 'Trash retention (days)', cacheRetention: 'Playback cache retention (minutes)',
    resourceLimits: 'Resource limits', cacheLimit: 'Playback cache limit (GiB)', minimumFree: 'Minimum free space (GiB)', mediaConcurrency: 'Concurrent background media jobs', playbackConcurrency: 'Concurrent playback window builds',
    registrationSettings: 'Registration settings', registrationEnabled: 'Allow registration', manageUsers: 'Manage users and invitation codes',
    registrationApprovalRequired: 'Require administrator approval', registrationApprovalDescription: 'When disabled, new users are approved automatically. Existing account statuses stay unchanged.',
    registrationInvitationRequired: 'Require an invitation code', registrationInvitationDescription: 'When disabled, local and first-time OAuth registrations do not need an invitation code.',
    registrationEmailVerificationRequired: 'Require email verification', registrationEmailVerificationDescription: 'When disabled, local and first-time OAuth registrations do not need an email verification code.',
    smtp: 'SMTP', host: 'Server', port: 'Port', ssl: 'SSL', userName: 'Username', password: 'Password (leave blank to keep)', fromAddress: 'Sender email', fromName: 'Sender name',
    checkCertificateRevocation: 'Check certificate revocation',
    certificateRevocationDescription: 'Enabled by default. Disable only when the revocation service is unreachable. Certificate trust, hostname and expiry are still checked, but the server cannot determine whether it has been revoked.',
    addProvider: 'Add provider', provider: 'OAuth provider', enabled: 'Enabled', deleteProvider: 'Delete provider', id: 'Identifier', name: 'Name',
    authorizationEndpoint: 'Authorization endpoint', tokenEndpoint: 'Token endpoint', userInfoEndpoint: 'UserInfo endpoint', callback: 'Callback URL',
    clientId: 'Client ID', clientSecret: 'Client secret (leave blank to keep)', scope: 'Scope', subjectField: 'User ID field', nameField: 'Name field', emailField: 'Email field', avatarField: 'Avatar field',
    basicAuthentication: 'HTTP Basic client authentication', noProviders: 'No OAuth providers configured', saved: 'Settings saved', invalidSegmentSize: 'File segment size must be greater than zero', invalidProvider: 'Enter an identifier and name for each OAuth provider',
  },
}

export const shellJa: MessageSchema<typeof shellZh> = {
  nav: {
    management: '管理', library: 'ライブラリ', clips: '自分のクリップ', jobs: 'バックグラウンドタスク', analytics: '配信分析', trash: 'ゴミ箱',
    targets: '録画管理', accounts: 'Bilibili アカウント', users: 'ユーザーと招待', settings: '設定', account: 'アカウント設定',
    playRecording: '録画の再生', streamerRecordings: '配信者の録画', toggleTheme: 'テーマを切り替え', userMenu: 'ユーザーメニュー', logout: 'ログアウト', logoutFailed: 'ログアウトできませんでした',
  },
  account: {
    title: 'アカウント設定', language: '表示言語', languageDescription: '言語設定はアカウントに保存され、ほかの端末でログインしたときも適用されます。',
    loginMethods: 'ログイン方法', bound: '連携済み', bind: '連携', noProviders: '外部ログイン方法はありません', languageSaved: '言語設定を保存しました',
  },
  settings: {
    title: '設定', save: '設定を保存', general: '全般', interface: '表示設定',
    defaultLanguageDescription: '全体の既定言語は、個人の言語を設定していないユーザーと、初めてサイトにアクセスする訪問者に適用されます。',
    recording: '録画', media: 'メディアとストレージ', registration: '登録', mail: 'メール', oauth: 'OAuth ログイン', recordingSettings: '録画設定',
    quality: '画質', recordDanmu: 'コメントを記録', segmentSize: 'ファイル分割サイズ（GiB）', monitorInterval: '配信開始の確認間隔（秒）', sessionGrace: '同じ配信への再接続待機時間（分）',
    filesAndTools: 'ファイルとツール', timeZone: 'ファイル名のタイムゾーン', ffmpeg: 'FFmpeg', ffprobe: 'FFprobe',
    retention: '保存期間', clipRetention: 'クリップの保存期間（日）', trashRetention: 'ゴミ箱の保存期間（日）', cacheRetention: '再生キャッシュの保存期間（分）',
    resourceLimits: 'リソース制限', cacheLimit: '再生キャッシュの上限（GiB）', minimumFree: '最小空き容量（GiB）', mediaConcurrency: 'メディアタスクの同時実行数', playbackConcurrency: '再生ウィンドウ生成の同時実行数',
    registrationSettings: '登録設定', registrationEnabled: '新規登録を許可', manageUsers: 'ユーザーと招待コードを管理',
    registrationApprovalRequired: '管理者の承認を必須にする', registrationApprovalDescription: '無効にすると、新規ユーザーは自動承認されます。既存アカウントの状態は変わりません。',
    registrationInvitationRequired: '招待コードを必須にする', registrationInvitationDescription: '無効にすると、通常登録と OAuth 初回登録のどちらも招待コードが不要になります。',
    registrationEmailVerificationRequired: 'メール認証コードを必須にする', registrationEmailVerificationDescription: '無効にすると、通常登録と OAuth 初回登録のどちらもメール認証コードが不要になります。',
    smtp: 'SMTP', host: 'サーバー', port: 'ポート', ssl: 'SSL', userName: 'ユーザー名', password: 'パスワード（空欄で変更なし）', fromAddress: '送信元メールアドレス', fromName: '送信者名',
    checkCertificateRevocation: '証明書の失効状態を確認',
    certificateRevocationDescription: '既定で有効です。失効確認サービスに接続できない場合のみ無効にしてください。無効にしても証明書の信頼性、ホスト名、有効期限を確認しますが、失効の有無は確認できません。',
    addProvider: 'プロバイダーを追加', provider: 'OAuth プロバイダー', enabled: '有効', deleteProvider: 'プロバイダーを削除', id: '識別子', name: '名前',
    authorizationEndpoint: '認可エンドポイント', tokenEndpoint: 'トークンエンドポイント', userInfoEndpoint: 'ユーザー情報エンドポイント', callback: 'コールバック URL',
    clientId: 'クライアント ID', clientSecret: 'クライアントシークレット（空欄で変更なし）', scope: 'スコープ', subjectField: 'ユーザー ID フィールド', nameField: '名前フィールド', emailField: 'メールフィールド', avatarField: 'アバターフィールド',
    basicAuthentication: 'HTTP Basic クライアント認証', noProviders: 'OAuth プロバイダーはありません', saved: '設定を保存しました', invalidSegmentSize: 'ファイル分割サイズはゼロより大きい値にしてください', invalidProvider: 'OAuth プロバイダーの識別子と名前を入力してください',
  },
}
