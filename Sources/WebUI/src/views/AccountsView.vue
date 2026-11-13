<template>
  <PageHeader :title="t('views.accounts.title')"><v-btn color="primary" prepend-icon="mdi-plus" @click="openLogin">{{ t('views.accounts.add') }}</v-btn></PageHeader>
  <v-alert v-if="error" type="error" class="mb-6">{{ error }}</v-alert>
  <div v-if="accounts.length" class="accounts-grid">
    <BilibiliAccountCard v-for="account in accounts" :key="account.id" :account="account" @remove="selected = $event" />
  </div>
  <EmptyState v-else-if="!busy" :title="t('views.accounts.empty')" icon="mdi-account-box-outline"><v-btn color="primary" @click="openLogin">{{ t('views.accounts.add') }}</v-btn></EmptyState>
  <v-dialog v-model="loginOpen" max-width="440" :aria-label="t('views.accounts.addBilibili')" @after-leave="resetLogin"><ContentCard :title="t('views.accounts.addBilibili')"><v-tabs v-model="loginMethod" color="primary" grow class="mb-4"><v-tab value="qr">{{ t('views.accounts.qrLogin') }}</v-tab><v-tab value="sms">{{ t('views.accounts.smsLogin') }}</v-tab></v-tabs><v-alert v-if="loginError" type="error" class="mb-5">{{ loginError }}</v-alert>
    <div v-if="loginMethod === 'qr'" class="qr-login">
      <v-progress-circular v-if="loginBusy" indeterminate color="primary" class="my-10" />
      <img v-else-if="qrImage && !['Expired', 'Complete', 'Failed'].includes(loginSession?.status ?? '')" :src="qrImage" width="220" height="220" :alt="t('views.accounts.qrAlt')" />
      <v-icon v-else :icon="loginSession?.status === 'Complete' ? 'mdi-check-circle-outline' : 'mdi-qrcode'" :color="loginSession?.status === 'Complete' ? 'success' : 'secondary'" size="80" class="my-9" />
      <div class="text-body-2 mt-5">{{ loginStatusLabel }}</div>
      <v-btn v-if="['Expired', 'Failed'].includes(loginSession?.status ?? '')" variant="tonal" color="primary" class="mt-5" @click="createQr">{{ t('views.accounts.refreshQr') }}</v-btn>
      <v-btn v-if="loginSession?.verificationUrl" :href="loginSession.verificationUrl" target="_blank" rel="noopener noreferrer" color="primary" class="mt-4">{{ t('views.accounts.verifyBilibili') }}</v-btn>
    </div>
    <form v-else class="form-stack" @submit.prevent="submitSms">
      <template v-if="!loginSession || !['Challenge', 'SmsSent', 'Complete', 'VerificationRequired'].includes(loginSession.status)"><div class="d-flex ga-3"><v-text-field v-model="countryCode" :label="t('views.accounts.countryCode')" style="max-width: 92px" prefix="+" required /><v-text-field v-model="phone" :label="t('views.accounts.phone')" inputmode="tel" autocomplete="tel-national" required /></div><v-btn type="submit" color="primary" :loading="loginBusy" block>{{ t('views.accounts.getCode') }}</v-btn></template>
      <CaptchaChallenge v-else-if="loginSession.status === 'Challenge' && loginSession.challenge" :challenge="loginSession.challenge" @verified="sendSms" />
      <template v-else-if="loginSession.status === 'SmsSent'"><v-text-field v-model="smsCode" :label="t('views.accounts.smsCode')" inputmode="numeric" autocomplete="one-time-code" required /><v-btn type="submit" color="primary" :loading="loginBusy" block>{{ t('views.common.login') }}</v-btn></template>
      <div v-else class="text-center py-6"><v-icon :icon="loginSession.status === 'Complete' ? 'mdi-check-circle-outline' : 'mdi-shield-check-outline'" color="primary" size="48" /><div class="mt-5">{{ loginStatusLabel }}</div><v-btn v-if="loginSession.verificationUrl" :href="loginSession.verificationUrl" target="_blank" rel="noopener noreferrer" color="primary" class="mt-5">{{ t('views.accounts.verifyBilibili') }}</v-btn></div>
    </form>
    <template #actions><v-btn variant="text" @click="loginOpen = false">{{ loginSession?.status === 'Complete' ? t('views.common.done') : t('views.common.close') }}</v-btn></template>
  </ContentCard></v-dialog>
  <ConfirmDialog :model-value="!!selected" :title="t('views.accounts.removeConfirm')" :message="selected?.userName" :action-label="t('views.common.remove')" :busy="busy" :error="error" @update:model-value="!$event && (selected = undefined)" @confirm="removeAccount" />
</template>

<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import ContentCard from '@/components/common/ContentCard.vue'
import QRCode from 'qrcode'
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { AuthAPI } from '@/apis/AuthAPI'
import { useAction } from '@/composables/useAction'
import { usePolling } from '@/composables/usePolling'
import type { BilibiliAccount, BilibiliLoginSession, SmsChallengeAnswer } from '@/models/auth'
import PageHeader from '@/components/common/PageHeader.vue'
import EmptyState from '@/components/common/EmptyState.vue'
import ConfirmDialog from '@/components/common/ConfirmDialog.vue'
import CaptchaChallenge from '@/components/accounts/CaptchaChallenge.vue'
import BilibiliAccountCard from '@/components/accounts/BilibiliAccountCard.vue'

const { t } = useI18n()

const accounts = ref<BilibiliAccount[]>([])
const selected = ref<BilibiliAccount>()
const loginOpen = ref(false)
const loginMethod = ref('qr')
const loginSession = ref<BilibiliLoginSession>()
const qrImage = ref('')
const countryCode = ref('86')
const phone = ref('')
const smsCode = ref('')
const { busy, error, run } = useAction()
const { busy: loginBusy, error: loginError, run: runLogin } = useAction()
let qrTimer: ReturnType<typeof setTimeout> | undefined
let loginGeneration = 0
const statusLabels = computed<Record<string, string>>(() => ({ Waiting: t('views.accounts.waiting'), Scanned: t('views.accounts.scanned'), Expired: t('views.accounts.expired'), Challenge: t('views.accounts.challenge'), SmsSent: t('views.accounts.codeSent'), Complete: t('views.accounts.complete'), VerificationRequired: t('views.accounts.verificationRequired'), Failed: t('views.accounts.failed') }))
const loginStatusLabel = computed(() => statusLabels.value[loginSession.value?.status ?? 'Waiting'] || loginSession.value?.message)
usePolling(async () => { await run(async () => { accounts.value = await AuthAPI.bilibiliAccounts() }) }, 30000)

function openLogin(): void
{
  resetLogin()
  loginMethod.value = 'qr'
  loginOpen.value = true
  void createQr()
}
function resetLogin(): void
{
  loginGeneration++
  clearTimeout(qrTimer)
  loginSession.value = undefined
  qrImage.value = ''
  loginError.value = ''
  smsCode.value = ''
}
watch(loginMethod, value =>
{
  resetLogin()
  if (value === 'qr' && loginOpen.value) { void createQr() }
}, { flush: 'sync' })
watch(loginOpen, value => { if (!value) { loginGeneration++; clearTimeout(qrTimer) } })
async function createQr(): Promise<void>
{
  const operation = ++loginGeneration
  clearTimeout(qrTimer)
  await runLogin(async () =>
  {
    const session = await AuthAPI.createQrLogin()
    if (operation !== loginGeneration) { return }
    loginSession.value = session
    qrImage.value = session.qrUrl ? await QRCode.toDataURL(session.qrUrl, { width: 280, margin: 2, errorCorrectionLevel: 'M' }) : ''
    qrTimer = setTimeout(() => { void pollQr(operation) }, 2000)
  })
}
async function pollQr(operation: number): Promise<void>
{
  if (!loginOpen.value || operation !== loginGeneration || !loginSession.value) { return }
  try
  {
    const session = await AuthAPI.pollQrLogin(loginSession.value.sessionId)
    if (operation !== loginGeneration) { return }
    loginSession.value = session
    if (session.status === 'Complete')
    {
      accounts.value = await AuthAPI.bilibiliAccounts()
      return
    }
    if (!['Expired', 'Failed'].includes(session.status))
    {
      qrTimer = setTimeout(() => { void pollQr(operation) }, 2000)
    }
  }
  catch (value)
  {
    loginError.value = value instanceof Error ? value.message : t('views.accounts.pollFailed')
  }
}
async function submitSms(): Promise<void>
{
  await runLogin(async () =>
  {
    if (loginSession.value?.status === 'SmsSent')
    {
      loginSession.value = await AuthAPI.completeSms(loginSession.value.sessionId, smsCode.value)
      if (loginSession.value.status === 'Complete') { accounts.value = await AuthAPI.bilibiliAccounts() }
    }
    else
    {
      loginSession.value = await AuthAPI.createSmsLogin(countryCode.value, phone.value)
      if (loginSession.value.challenge && loginSession.value.status !== 'Challenge') { loginSession.value.status = 'Challenge' }
    }
  })
}
async function sendSms(answer: SmsChallengeAnswer): Promise<void>
{
  if (!loginSession.value) { return }
  await runLogin(async () => { loginSession.value = await AuthAPI.sendSms(loginSession.value!.sessionId, answer) })
}
async function removeAccount(): Promise<void>
{
  if (!selected.value) { return }
  await run(async () => { await AuthAPI.deleteBilibiliAccount(selected.value!.id); selected.value = undefined; accounts.value = await AuthAPI.bilibiliAccounts() })
}
onBeforeUnmount(resetLogin)
</script>

<style scoped>
.accounts-grid
{
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(min(100%, 340px), 1fr));
  gap: 20px;
}
.qr-login
{
  display: flex;
  flex-direction: column;
  align-items: center;
  min-height: 280px;
}
.qr-login img
{
  border-radius: 12px;
  background: white;
}
</style>
