<template>
  <PageHeader title="设置"><v-btn color="primary" prepend-icon="mdi-check" :loading="saving" :disabled="!settings" @click="save">保存设置</v-btn></PageHeader>
  <v-alert v-if="error || saveError" type="error" class="mb-6">{{ error || saveError }}</v-alert>
  <v-progress-linear v-if="loading" indeterminate color="primary" />
  <template v-if="settings">
    <v-tabs v-model="tab" color="primary" show-arrows class="mb-6"><v-tab value="recording">录制</v-tab><v-tab value="media">媒体与存储</v-tab><v-tab value="registration">注册</v-tab><v-tab value="smtp">邮件</v-tab><v-tab value="oauth">OAuth 登录</v-tab></v-tabs>
    <div v-if="tab === 'recording'" class="settings-grid">
      <v-card class="form-section"><h2>录制设置</h2><div class="form-stack"><v-select v-model="settings.quality" label="清晰度" :items="QUALITY_OPTIONS" /><v-switch v-model="settings.recordDanmu" label="录制弹幕" /><v-text-field v-model.number="segmentGiB" label="文件分段大小（GiB）" type="number" min="0.1" step="0.5" /><v-text-field v-model.number="settings.monitorIntervalSeconds" label="开播检测间隔（秒）" type="number" min="5" /><v-text-field v-model.number="settings.sessionGraceMinutes" label="同场重连等待（分钟）" type="number" min="1" /></div></v-card>
      <v-card class="form-section"><h2>文件与工具</h2><div class="form-stack"><v-text-field v-model="settings.storagePath" label="录播目录" /><v-text-field v-model="settings.timeZone" label="文件命名时区" /><v-text-field v-model="settings.ffmpegPath" label="FFmpeg" /><v-text-field v-model="settings.ffprobePath" label="FFprobe" /></div></v-card>
    </div>
    <div v-else-if="tab === 'media'" class="settings-grid">
      <v-card class="form-section"><h2>保留时间</h2><div class="form-stack"><v-text-field v-model.number="settings.clipRetentionDays" label="切片保留（天）" type="number" min="1" /><v-text-field v-model.number="settings.trashRetentionDays" label="回收站保留（天）" type="number" min="1" /><v-text-field v-model.number="settings.cacheRetentionMinutes" label="播放缓存保留（分钟）" type="number" min="1" /></div></v-card>
      <v-card class="form-section"><h2>资源限制</h2><div class="form-stack"><v-text-field v-model.number="cacheGiB" label="播放缓存上限（GiB）" type="number" min="1" /><v-text-field v-model.number="minimumFreeGiB" label="最小空闲空间（GiB）" type="number" min="1" /><v-text-field v-model.number="settings.mediaConcurrency" label="后台媒体任务并发" type="number" min="1" max="32" /><v-text-field v-model.number="settings.playbackConcurrency" label="播放窗口生成并发" type="number" min="1" max="32" /></div></v-card>
    </div>
    <v-card v-else-if="tab === 'registration'" class="form-section" style="max-width: 660px"><h2>注册设置</h2><v-switch v-model="settings.registrationEnabled" label="开放注册" /><v-divider class="my-6" /><v-btn variant="tonal" prepend-icon="mdi-account-group-outline" to="/admin/users">管理用户与邀请码</v-btn></v-card>
    <v-card v-else-if="tab === 'smtp'" class="form-section" style="max-width: 800px"><h2>SMTP</h2><div class="form-stack"><div class="d-flex ga-4"><v-text-field v-model="settings.smtp.host" label="服务器" /><v-text-field v-model.number="settings.smtp.port" label="端口" type="number" min="1" max="65535" style="max-width: 130px" /></div><v-switch v-model="settings.smtp.useSsl" label="SSL" /><v-text-field v-model="settings.smtp.userName" label="用户名" autocomplete="off" /><v-text-field v-model="settings.smtp.password" label="密码（留空保留）" type="password" autocomplete="new-password" /><v-text-field v-model="settings.smtp.fromAddress" label="发件邮箱" type="email" /><v-text-field v-model="settings.smtp.fromName" label="发件人" /></div></v-card>
    <template v-else-if="tab === 'oauth'">
      <div class="d-flex justify-end mb-5"><v-btn variant="tonal" prepend-icon="mdi-plus" @click="addProvider">添加提供方</v-btn></div>
      <div class="form-stack"><v-card v-for="(provider, index) in settings.oauthProviders" :key="index" class="form-section"><div class="d-flex align-center justify-space-between mb-6"><h2 class="mb-0">{{ provider.name || 'OAuth 提供方' }}</h2><div class="d-flex align-center ga-3"><v-switch v-model="provider.enabled" label="启用" density="compact" /><v-btn icon="mdi-delete-outline" variant="text" size="small" aria-label="删除提供方" @click="settings.oauthProviders.splice(index, 1)" /></div></div><div class="settings-grid"><div class="form-stack"><v-text-field v-model="provider.id" label="标识" /><v-text-field v-model="provider.name" label="名称" /><v-text-field v-model="provider.authorizationEndpoint" label="授权端点" /><v-text-field v-model="provider.tokenEndpoint" label="Token 端点" /><v-text-field v-model="provider.userInfoEndpoint" label="UserInfo 端点" /><v-text-field :model-value="`${origin}/oauth/callback/${provider.id}`" label="回调地址" readonly /></div><div class="form-stack"><v-text-field v-model="provider.clientId" label="Client ID" autocomplete="off" /><v-text-field v-model="provider.clientSecret" label="Client Secret（留空保留）" type="password" autocomplete="new-password" /><v-text-field v-model="provider.scope" label="Scope" /><v-text-field v-model="provider.subjectField" label="用户 ID 字段" /><v-text-field v-model="provider.nameField" label="名称字段" /><div class="d-flex ga-4"><v-text-field v-model="provider.emailField" label="邮箱字段" /><v-text-field v-model="provider.avatarField" label="头像字段" /></div><v-switch v-model="provider.useBasicAuthentication" label="HTTP Basic 客户端认证" /></div></div></v-card></div>
      <EmptyState v-if="!settings.oauthProviders.length" title="暂无 OAuth 提供方" icon="mdi-account-key-outline" />
    </template>
  </template>
  <v-snackbar v-model="saved" color="success">设置已保存</v-snackbar>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { MediaAPI } from '@/apis/MediaAPI'
import { useAction } from '@/composables/useAction'
import { useAuthStore } from '@/stores/auth'
import type { ServerSettings } from '@/models/settings'
import { QUALITY_OPTIONS } from '@/models/settings'
import PageHeader from '@/components/common/PageHeader.vue'
import EmptyState from '@/components/common/EmptyState.vue'

const tab = ref('recording')
const settings = ref<ServerSettings>()
const saved = ref(false)
const origin = window.location.origin
const { busy: loading, error, run } = useAction()
const { busy: saving, error: saveError, run: runSave } = useAction()
const auth = useAuthStore()
const byteField = (key: 'segmentBytes' | 'cacheLimitBytes' | 'minimumFreeBytes') => computed({
  get: () => settings.value ? settings.value[key] / 1024 ** 3 : 0,
  set: (value: number) => { if (settings.value) { settings.value[key] = Math.round(Number(value) * 1024 ** 3) } },
})
const segmentGiB = byteField('segmentBytes')
const cacheGiB = byteField('cacheLimitBytes')
const minimumFreeGiB = byteField('minimumFreeBytes')
function clearSecrets(value: ServerSettings): ServerSettings
{
  value.smtp.password = ''
  value.oauthProviders ??= []
  for (const provider of value.oauthProviders) { provider.clientSecret = '' }
  return value
}
onMounted(() => { void run(async () => { settings.value = clearSecrets(await MediaAPI.settings()) }) })
function addProvider(): void
{
  settings.value?.oauthProviders.push({ id: '', name: '', enabled: false, authorizationEndpoint: '', tokenEndpoint: '', userInfoEndpoint: '', clientId: '', clientSecret: '', scope: 'profile email', subjectField: 'sub', nameField: 'displayName', emailField: 'email', avatarField: 'avatarUrl', useBasicAuthentication: false })
}
async function save(): Promise<void>
{
  if (!settings.value) { return }
  await runSave(async () =>
  {
    if (!Number.isFinite(settings.value!.segmentBytes) || settings.value!.segmentBytes <= 0) { throw new Error('文件分段大小必须大于零') }
    if (settings.value!.oauthProviders.some(provider => !provider.id.trim() || !provider.name.trim())) { throw new Error('请填写 OAuth 提供方标识和名称') }
    await MediaAPI.updateSettings(settings.value!)
    settings.value = clearSecrets(await MediaAPI.settings())
    auth.providers = await (await import('@/apis/AuthAPI')).AuthAPI.providers()
    saved.value = true
  })
}
</script>
