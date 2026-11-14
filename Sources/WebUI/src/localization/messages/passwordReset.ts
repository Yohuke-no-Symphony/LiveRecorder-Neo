import type { MessageSchema } from '@/localization/types'

export const passwordResetZh = {
  forgot: '忘记密码', title: '重设密码', requestDescription: '输入账户登记的邮箱，我们会发送重设密码链接。',
  send: '发送重设链接', sent: '如果该邮箱已注册，你将收到重设密码邮件，请检查收件箱。',
  unavailable: '本站暂未启用邮件密码恢复，请联系管理员。', newPassword: '新密码', confirmPassword: '确认新密码',
  passwordLength: '密码长度必须为 12 至 256 个字符', mismatch: '两次输入的密码不一致',
  submit: '重设密码', success: '密码已重设，请使用新密码登录。', invalidLink: '重设链接无效，请重新申请。',
  requestAgain: '重新申请链接', expiredHint: '链接有效期为 30 分钟，只能成功重设一次。',
  publicWebUrl: '站点公开地址', publicWebUrlDescription: '用于生成密码恢复邮件中的链接，例如 https://recorder.example.com。',
}

export const passwordResetEn: MessageSchema<typeof passwordResetZh> = {
  forgot: 'Forgot password', title: 'Reset password', requestDescription: 'Enter your registered email address to request a password reset link.',
  send: 'Send reset link', sent: 'If this email is registered, you will receive a password reset email. Please check your inbox.',
  unavailable: 'Email password recovery is not available. Please contact the administrator.', newPassword: 'New password', confirmPassword: 'Confirm new password',
  passwordLength: 'Passwords must contain 12 to 256 characters', mismatch: 'The passwords do not match',
  submit: 'Reset password', success: 'Your password has been reset. Sign in with your new password.', invalidLink: 'This reset link is invalid. Please request a new one.',
  requestAgain: 'Request a new link', expiredHint: 'Links expire after 30 minutes and can complete one password reset.',
  publicWebUrl: 'Public website address', publicWebUrlDescription: 'Used for password recovery email links, for example https://recorder.example.com.',
}

export const passwordResetJa: MessageSchema<typeof passwordResetZh> = {
  forgot: 'パスワードを忘れた場合', title: 'パスワードの再設定', requestDescription: '登録済みのメールアドレスに、パスワード再設定用のリンクを送信します。',
  send: '再設定リンクを送信', sent: '登録済みのメールアドレスであれば、パスワード再設定メールが届きます。受信箱をご確認ください。',
  unavailable: 'メールによるパスワード再設定は現在利用できません。管理者にお問い合わせください。', newPassword: '新しいパスワード', confirmPassword: '新しいパスワード（確認）',
  passwordLength: 'パスワードは 12～256 文字で入力してください', mismatch: 'パスワードが一致しません',
  submit: 'パスワードを再設定', success: 'パスワードを再設定しました。新しいパスワードでログインしてください。', invalidLink: '再設定リンクが無効です。もう一度申請してください。',
  requestAgain: 'リンクを再申請', expiredHint: 'リンクの有効期限は 30 分で、再設定できるのは一度だけです。',
  publicWebUrl: 'サイトの公開アドレス', publicWebUrlDescription: 'パスワード再設定メールのリンクに使用します。例：https://recorder.example.com。',
}
