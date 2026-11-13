import type { MessageSchema } from '@/localization/types'

export const componentsZh = {
  common: {
    cancel: '取消', confirm: '确认', retry: '重试', loadMore: '加载更多', operationFailed: '操作失败',
    emailCode: '邮箱验证码', sendCode: '发送验证码', countdown: '{seconds}秒',
  },
  status: {
    recording: '录制中', live: '直播中', monitoring: '监听中', waiting: '等待开播', paused: '已暂停',
    reconnecting: '重连中', error: '异常', stopped: '已停止', offline: '未开播', queued: '排队中',
    pending: '待审核', running: '处理中', publishing: '发布中', completed: '已完成', ready: '可播放',
    failed: '失败', interrupted: '已中断', canceled: '已取消', approved: '已通过', rejected: '已拒绝',
    disabled: '已禁用', expired: '已过期', valid: '有效', invalid: '登录失效', needsLogin: '需重新登录', trashed: '回收站',
  },
  storage: {
    disk: '磁盘', usedPercent: '磁盘已用 {percent}%', freeBytes: '{bytes} 空余', used: '占用 {bytes}',
    trash: '回收站 {bytes}', free: '空余 {bytes}',
  },
  dataDirectories: {
    title: '存储目录', data: '数据目录', records: '录像目录',
    description: '所有后台配置保存在同一个 JSON 配置文件中。数据目录保存密钥、缓存和安装状态，录像目录可独立设置。安装时会创建缺少的目录。',
    active: '当前数据目录', activeRecords: '当前录像目录', configFile: '配置文件', save: '保存目录配置', saved: '目录配置已保存',
    relativePath: '两个目录的相对路径均以配置文件所在目录为基准。',
    migration: '保存操作只更新配置，不会移动文件。请手动迁移相关数据和录像，目录变更将在重启后生效。',
    restartRequired: '目录变更待重启后生效，服务继续使用上方显示的当前目录。',
  },
  events: {
    title: '直播消息', follow: '跟随', all: '全部', danmu: '弹幕', superChat: 'SC', gift: '礼物', guard: '舰长',
    redPocket: '红包', gap: '录制中断', guest: '访客', empty: '当前时段暂无消息', notRecorded: '此场录播未保存弹幕',
    count: '{count} 条', messageCount: '{count} 条消息', loadFailed: '消息加载失败', roomAdmin: '房管',
    reply: '回复 {name}：', governor: '总督', admiral: '提督', captain: '舰长', membership: '大航海', quote: '报价',
    drawn: '已开奖', drawnWithoutResult: '已开奖，暂无结果', pendingDraw: '待开奖', award: '奖品',
    awardCount: '{count} 份', drawAt: '开奖 {date}', mystery: '神秘人', battery: '{count} 电池', intimacy: '{count} 亲密度',
  },
  player: {
    label: '录播播放器', position: '播放位置', play: '播放', pause: '暂停', rewind: '后退10秒', forward: '前进10秒',
    chaseLive: '追录', mute: '静音', unmute: '取消静音', volume: '音量', fullscreen: '全屏', closePreview: '关闭预览',
    previewFailed: '无法播放此切片', noVideo: '当前时段没有可播放的视频', loadFailed: '视频加载失败',
    unsupported: '当前浏览器无法播放此录播', createClip: '创建切片', name: '名称', clipRange: '切片范围',
    startTime: '开始时间', endTime: '结束时间', exact: '精确', fast: '快速', createJob: '创建任务',
    invalidClipRange: '请输入有效的切片起止时间',
  },
  library: {
    loadFailed: '录播加载失败', invalidPage: '录播分页响应无效', refreshFailed: '录播刷新失败',
    unnamedSession: '未命名录播', hasDanmu: '包含弹幕', live: '正在直播', room: '直播间 {roomId}',
    splitTitle: '拆分场次', splitBoundary: '拆分起始片段', segment: '片段 {number} · {time} · {size}',
    noBoundaries: '暂无可拆分片段', split: '拆分', groupTitle: '归组场次', selected: '已选 {count}',
    groupName: '归组标题', searchSessions: '搜索场次', selectSession: '选择 {title}', noCandidates: '暂无可选场次', group: '归组',
  },
  chart: {
    title: '直播趋势', messages: '消息', amount: '观测金额', empty: '暂无数据', amountTrend: '直播观测金额趋势',
    messageTrend: '直播消息数量趋势',
  },
  captcha: {
    imageCode: '图片验证码', verifyAndSend: '验证并发送短信', verify: '完成安全验证',
    loadFailed: '安全验证加载失败', unavailable: '安全验证不可用', failed: '安全验证失败，请重试',
  },
  quality: { original: '原画', bluRay: '蓝光', ultra: '超清', high: '高清', smooth: '流畅' },
} as const

export const componentsEn: MessageSchema<typeof componentsZh> = {
  common: {
    cancel: 'Cancel', confirm: 'Confirm', retry: 'Retry', loadMore: 'Load more', operationFailed: 'Operation failed',
    emailCode: 'Email verification code', sendCode: 'Send code', countdown: '{seconds}s',
  },
  status: {
    recording: 'Recording', live: 'Live', monitoring: 'Monitoring', waiting: 'Waiting for stream', paused: 'Paused',
    reconnecting: 'Reconnecting', error: 'Error', stopped: 'Stopped', offline: 'Offline', queued: 'Queued',
    pending: 'Pending approval', running: 'Processing', publishing: 'Publishing', completed: 'Completed', ready: 'Ready to play',
    failed: 'Failed', interrupted: 'Interrupted', canceled: 'Canceled', approved: 'Approved', rejected: 'Rejected',
    disabled: 'Disabled', expired: 'Expired', valid: 'Active', invalid: 'Login expired', needsLogin: 'Login required', trashed: 'Trash',
  },
  storage: {
    disk: 'Disk', usedPercent: 'Disk {percent}% used', freeBytes: '{bytes} free', used: 'Used {bytes}',
    trash: 'Trash {bytes}', free: 'Free {bytes}',
  },
  dataDirectories: {
    title: 'Storage directories', data: 'Data directory', records: 'Recordings directory',
    description: 'All server configuration is stored in one JSON file. The data directory stores keys, cache, and installation state. Recordings can use a separate directory. Setup creates missing directories.',
    active: 'Current data directory', activeRecords: 'Current recordings directory', configFile: 'Configuration file', save: 'Save directory configuration', saved: 'Directory configuration saved',
    relativePath: 'Relative paths for both directories are resolved from the directory containing the configuration file.',
    migration: 'Saving updates the configuration and does not move files. Move the data and recordings manually; directory changes take effect after a restart.',
    restartRequired: 'A restart is required to apply the directory changes. The server still uses the current directories shown above.',
  },
  events: {
    title: 'Live messages', follow: 'Follow', all: 'All', danmu: 'Chat', superChat: 'SC', gift: 'Gifts', guard: 'Memberships',
    redPocket: 'Red packets', gap: 'Recording interrupted', guest: 'Guest', empty: 'No messages in this period',
    notRecorded: 'Chat was not recorded for this session', count: '{count} messages', messageCount: '{count} messages',
    loadFailed: 'Failed to load messages', roomAdmin: 'Moderator', reply: 'Reply to {name}: ', governor: 'Governor',
    admiral: 'Admiral', captain: 'Captain', membership: 'Membership', quote: 'Quoted', drawn: 'Drawn',
    drawnWithoutResult: 'Drawn; results unavailable', pendingDraw: 'Awaiting draw', award: 'Prize', awardCount: '{count} prizes',
    drawAt: 'Draw at {date}', mystery: 'Anonymous', battery: '{count} batteries', intimacy: '{count} affinity',
  },
  player: {
    label: 'Recording player', position: 'Playback position', play: 'Play', pause: 'Pause', rewind: 'Back 10 seconds',
    forward: 'Forward 10 seconds', chaseLive: 'Go to live recording', mute: 'Mute', unmute: 'Unmute', volume: 'Volume',
    fullscreen: 'Fullscreen', closePreview: 'Close preview', previewFailed: 'Unable to play this clip',
    noVideo: 'No playable video in this period', loadFailed: 'Failed to load video', unsupported: 'This browser cannot play this recording',
    createClip: 'Create clip', name: 'Name', clipRange: 'Clip range', startTime: 'Start time', endTime: 'End time',
    exact: 'Exact', fast: 'Fast', createJob: 'Create task', invalidClipRange: 'Enter valid clip start and end times',
  },
  library: {
    loadFailed: 'Failed to load recordings', invalidPage: 'Invalid recording page response', refreshFailed: 'Failed to refresh recordings',
    unnamedSession: 'Untitled recording', hasDanmu: 'Includes chat', live: 'Live now', room: 'Room {roomId}',
    splitTitle: 'Split session', splitBoundary: 'First segment of the new session', segment: 'Segment {number} · {time} · {size}',
    noBoundaries: 'No segments available to split', split: 'Split', groupTitle: 'Group sessions', selected: '{count} selected',
    groupName: 'Group title', searchSessions: 'Search sessions', selectSession: 'Select {title}',
    noCandidates: 'No sessions available', group: 'Group',
  },
  chart: {
    title: 'Stream activity', messages: 'Messages', amount: 'Observed amount', empty: 'No data',
    amountTrend: 'Observed stream amount over time', messageTrend: 'Stream message count over time',
  },
  captcha: {
    imageCode: 'Image verification code', verifyAndSend: 'Verify and send SMS', verify: 'Complete verification',
    loadFailed: 'Failed to load verification', unavailable: 'Verification is unavailable', failed: 'Verification failed. Try again.',
  },
  quality: { original: 'Original', bluRay: 'Blu-ray', ultra: 'Ultra HD', high: 'HD', smooth: 'Fluent' },
}

export const componentsJa: MessageSchema<typeof componentsZh> = {
  common: {
    cancel: 'キャンセル', confirm: '確認', retry: '再試行', loadMore: 'もっと見る', operationFailed: '操作に失敗しました',
    emailCode: 'メール認証コード', sendCode: '認証コードを送信', countdown: '{seconds}秒',
  },
  status: {
    recording: '録画中', live: '配信中', monitoring: '監視中', waiting: '配信開始待ち', paused: '一時停止中',
    reconnecting: '再接続中', error: 'エラー', stopped: '停止済み', offline: 'オフライン', queued: '待機中',
    pending: '承認待ち', running: '処理中', publishing: '公開処理中', completed: '完了', ready: '再生可能',
    failed: '失敗', interrupted: '中断', canceled: 'キャンセル済み', approved: '承認済み', rejected: '却下済み',
    disabled: '無効', expired: '期限切れ', valid: '有効', invalid: 'ログイン期限切れ', needsLogin: '再ログインが必要', trashed: 'ゴミ箱',
  },
  storage: {
    disk: 'ディスク', usedPercent: 'ディスク使用率 {percent}%', freeBytes: '空き {bytes}', used: '使用中 {bytes}',
    trash: 'ゴミ箱 {bytes}', free: '空き {bytes}',
  },
  dataDirectories: {
    title: '保存ディレクトリ', data: 'データディレクトリ', records: '録画ディレクトリ',
    description: 'すべてのサーバー設定を1つの JSON ファイルに保存します。データディレクトリには鍵、キャッシュ、インストール状態を保存し、録画は別のディレクトリを指定できます。インストール時に存在しないディレクトリを作成します。',
    active: '現在のデータディレクトリ', activeRecords: '現在の録画ディレクトリ', configFile: '設定ファイル', save: 'ディレクトリ設定を保存', saved: 'ディレクトリ設定を保存しました',
    relativePath: '両方のディレクトリの相対パスは、設定ファイルがあるディレクトリを基準に解決します。',
    migration: '保存では設定だけを更新し、ファイルは移動しません。データと録画を手動で移行してください。ディレクトリの変更は再起動後に反映されます。',
    restartRequired: 'ディレクトリの変更には再起動が必要です。現在は上に表示されているディレクトリを使用しています。',
  },
  events: {
    title: '配信メッセージ', follow: '追従', all: 'すべて', danmu: 'コメント', superChat: 'SC', gift: 'ギフト', guard: 'メンバーシップ',
    redPocket: '紅包', gap: '録画が中断されました', guest: 'ゲスト', empty: 'この時間帯にメッセージはありません',
    notRecorded: 'この配信ではコメントを記録していません', count: '{count} 件', messageCount: 'メッセージ {count} 件',
    loadFailed: 'メッセージを読み込めませんでした', roomAdmin: 'モデレーター', reply: '{name} への返信：', governor: '総督',
    admiral: '提督', captain: '艦長', membership: '大航海', quote: '参考価格', drawn: '抽選済み',
    drawnWithoutResult: '抽選済み・結果未取得', pendingDraw: '抽選待ち', award: '賞品', awardCount: '{count} 個',
    drawAt: '抽選日時 {date}', mystery: '匿名ユーザー', battery: '電池 {count} 個', intimacy: '親密度 {count}',
  },
  player: {
    label: '録画プレーヤー', position: '再生位置', play: '再生', pause: '一時停止', rewind: '10秒戻る', forward: '10秒進む',
    chaseLive: '録画中の位置へ', mute: 'ミュート', unmute: 'ミュート解除', volume: '音量', fullscreen: '全画面',
    closePreview: 'プレビューを閉じる', previewFailed: 'このクリップを再生できません', noVideo: 'この時間帯に再生可能な動画はありません',
    loadFailed: '動画を読み込めませんでした', unsupported: 'このブラウザーでは録画を再生できません',
    createClip: 'クリップを作成', name: '名前', clipRange: 'クリップの範囲', startTime: '開始時間', endTime: '終了時間',
    exact: '正確', fast: '高速', createJob: 'タスクを作成', invalidClipRange: '有効なクリップの開始・終了時間を入力してください',
  },
  library: {
    loadFailed: '録画を読み込めませんでした', invalidPage: '録画のページ応答が無効です', refreshFailed: '録画を更新できませんでした',
    unnamedSession: '無題の録画', hasDanmu: 'コメント付き', live: '配信中', room: '配信ルーム {roomId}',
    splitTitle: '配信回を分割', splitBoundary: '新しい配信回の先頭セグメント', segment: 'セグメント {number} · {time} · {size}',
    noBoundaries: '分割可能なセグメントはありません', split: '分割', groupTitle: '配信回をグループ化', selected: '{count} 件選択中',
    groupName: 'グループ名', searchSessions: '配信回を検索', selectSession: '{title} を選択',
    noCandidates: '選択可能な配信回はありません', group: 'グループ化',
  },
  chart: {
    title: '配信の推移', messages: 'メッセージ', amount: '観測金額', empty: 'データがありません',
    amountTrend: '配信の観測金額の推移', messageTrend: '配信メッセージ数の推移',
  },
  captcha: {
    imageCode: '画像認証コード', verifyAndSend: '認証してSMSを送信', verify: 'セキュリティ認証',
    loadFailed: '認証を読み込めませんでした', unavailable: '認証を利用できません', failed: '認証に失敗しました。再試行してください',
  },
  quality: { original: 'オリジナル', bluRay: 'ブルーレイ', ultra: '超高画質', high: '高画質', smooth: '低画質' },
}
