<template>
  <PageHeader :title="t('views.accounts.title')">
    <v-btn color="primary" prepend-icon="mdi-plus" @click="openLogin()">{{ t('views.accounts.add') }}</v-btn>
  </PageHeader>
  <v-alert v-if="error" type="error" class="mb-6">{{ error }}</v-alert>
  <v-alert v-if="fallbackAccount" type="info" class="mb-6">{{ t('accountManagement.fallback', { name:
    fallbackAccount.userName }) }}
  </v-alert>
  <div v-if="accounts.length" class="accounts-grid">
    <BilibiliAccountCard v-for="account in accounts" :key="account.id" :account="account" :busy="busy"
                         @remove="selected = $event" @relogin="openLogin($event)" @set-default="setDefault"/>
  </div>
  <EmptyState v-else-if="!busy" :title="t('views.accounts.empty')" icon="mdi-account-box-outline">
    <v-btn color="primary" @click="openLogin()">{{ t('views.accounts.add') }}</v-btn>
  </EmptyState>
  <v-dialog v-model="loginOpen" max-width="440" :aria-label="loginTitle">
    <ContentCard :title="loginTitle"><p v-if="loginTarget" class="text-body-2 mb-4">{{
      t('accountManagement.reloginIdentity', { name: loginTarget.userName, uid: loginTarget.uid }) }}</p>
      <v-tabs v-model="loginMethod" color="primary" grow class="mb-4">
        <v-tab value="qr">{{ t('views.accounts.qrLogin') }}</v-tab>
        <v-tab value="sms">{{ t('views.accounts.smsLogin') }}</v-tab>
      </v-tabs>
      <v-alert v-if="loginError" type="error" class="mb-5">{{ loginError }}</v-alert>
      <div v-if="loginMethod === 'qr'" class="qr-login">
        <v-progress-circular v-if="loginBusy" indeterminate color="primary" class="my-10"/>
        <img v-else-if="qrImage && !['Expired', 'Complete', 'Failed'].includes(loginSession?.status ?? '')"
             :src="qrImage" width="220" height="220" :alt="t('views.accounts.qrAlt')"/>
        <v-icon v-else :icon="loginSession?.status === 'Complete' ? 'mdi-check-circle-outline' : 'mdi-qrcode'"
                :color="loginSession?.status === 'Complete' ? 'success' : 'secondary'" size="80" class="my-9"/>
        <div class="text-body-2 mt-5">{{ loginStatusLabel }}</div>
        <v-btn v-if="!loginBusy && (loginError || ['Expired', 'Failed'].includes(loginSession?.status ?? ''))"
               variant="tonal" color="primary" class="mt-5" @click="createQr">{{ t('views.accounts.refreshQr') }}
        </v-btn>
        <v-btn v-if="loginSession?.verificationUrl" :href="loginSession.verificationUrl" target="_blank"
               rel="noopener noreferrer" color="primary" class="mt-4">{{ t('views.accounts.verifyBilibili') }}
        </v-btn>
      </div>
      <form v-else class="form-stack" @submit.prevent="submitSms">
        <template
          v-if="!loginSession || !['Challenge', 'SmsSent', 'Complete', 'VerificationRequired'].includes(loginSession.status)">
          <div class="d-flex ga-3">
            <v-text-field v-model="countryCode" :label="t('views.accounts.countryCode')" style="max-width: 92px"
                          prefix="+" required/>
            <v-text-field v-model="phone" :label="t('views.accounts.phone')" inputmode="tel" autocomplete="tel-national"
                          required/>
          </div>
          <v-btn type="submit" color="primary" :loading="loginBusy" block>{{ t('views.accounts.getCode') }}</v-btn>
        </template>
        <CaptchaChallenge v-else-if="loginSession.status === 'Challenge' && loginSession.challenge"
                          :challenge="loginSession.challenge" @verified="sendSms"/>
        <template v-else-if="loginSession.status === 'SmsSent'">
          <v-text-field v-model="smsCode" :label="t('views.accounts.smsCode')" inputmode="numeric"
                        autocomplete="one-time-code" required/>
          <v-btn type="submit" color="primary" :loading="loginBusy" block>{{ t('views.common.login') }}</v-btn>
        </template>
        <div v-else class="text-center py-6">
          <v-icon :icon="loginSession.status === 'Complete' ? 'mdi-check-circle-outline' : 'mdi-shield-check-outline'"
                  color="primary" size="48"/>
          <div class="mt-5">{{ loginStatusLabel }}</div>
          <v-btn v-if="loginSession.verificationUrl" :href="loginSession.verificationUrl" target="_blank"
                 rel="noopener noreferrer" color="primary" class="mt-5">{{ t('views.accounts.verifyBilibili') }}
          </v-btn>
        </div>
      </form>
      <template #actions>
        <v-btn variant="text" @click="loginOpen = false">{{ loginSession?.status === 'Complete' ? t('views.common.done')
          : t('views.common.close') }}
        </v-btn>
      </template>
    </ContentCard>
  </v-dialog>
  <ConfirmDialog :model-value="!!selected" :title="t('views.accounts.removeConfirm')" :message="selected?.userName"
                 :action-label="t('views.common.remove')" :busy="busy" :error="error"
                 @update:model-value="!$event && (selected = undefined)" @confirm="removeAccount"/>
</template>

<script setup lang="ts">
import {useI18n} from 'vue-i18n'
import ContentCard from '@/components/common/ContentCard.vue'
import QRCode from 'qrcode'
import {computed, onBeforeUnmount, ref, watch} from 'vue'
import {AuthAPI} from '@/apis/AuthAPI'
import {useAction} from '@/composables/useAction'
import {usePolling} from '@/composables/usePolling'
import type {BilibiliAccount, BilibiliLoginSession, SmsChallengeAnswer} from '@/models/auth'
import PageHeader from '@/components/common/PageHeader.vue'
import EmptyState from '@/components/common/EmptyState.vue'
import ConfirmDialog from '@/components/common/ConfirmDialog.vue'
import CaptchaChallenge from '@/components/accounts/CaptchaChallenge.vue'
import BilibiliAccountCard from '@/components/accounts/BilibiliAccountCard.vue'

const {t} = useI18n()

const accounts = ref<BilibiliAccount[]>([])
const selected = ref<BilibiliAccount>()
const loginTarget = ref<BilibiliAccount>()
const loginOpen = ref(false)
const loginMethod = ref('qr')
const loginSession = ref<BilibiliLoginSession>()
const qrImage = ref('')
const countryCode = ref('86')
const phone = ref('')
const smsCode = ref('')
const {busy, error, run} = useAction()
const loginBusy = ref(false)
const loginError = ref('')
let qrTimer: ReturnType<typeof setTimeout> | undefined
let loginGeneration = 0
let accountsGeneration = 0
const loginTitle = computed(() => t(loginTarget.value ? 'accountManagement.reloginTitle' : 'views.accounts.addBilibili'))
const fallbackAccount = computed(() => accounts.value.some(account => account.isDefault) ? accounts.value.find(account => account.isEffectiveDefault && !account.isDefault) : undefined)
const statusLabels = computed<Record<string, string>>(() => ({
  Waiting: t('views.accounts.waiting'),
  Scanned: t('views.accounts.scanned'),
  Expired: t('views.accounts.expired'),
  Challenge: t('views.accounts.challenge'),
  SmsSent: t('views.accounts.codeSent'),
  Complete: t('views.accounts.complete'),
  VerificationRequired: t('views.accounts.verificationRequired'),
  Failed: t('views.accounts.failed')
}))
const loginStatusLabel = computed(() => statusLabels.value[loginSession.value?.status ?? 'Waiting'] || loginSession.value?.message)
usePolling(async () => {
  await run(() => refreshAccounts())
}, 30000)

async function refreshAccounts(isValid: () => boolean = () => true): Promise<void> {
  const operation = ++accountsGeneration
  const result = await AuthAPI.bilibiliAccounts()
  if (operation === accountsGeneration && isValid()) {
    accounts.value = result
  }
}

function openLogin(account?: BilibiliAccount): void {
  loginOpen.value = false
  resetLogin()
  loginTarget.value = account
  loginMethod.value = 'qr'
  loginOpen.value = true
  void createQr()
}

function resetLogin(): void {
  loginGeneration++
  clearTimeout(qrTimer)
  loginSession.value = undefined
  qrImage.value = ''
  loginError.value = ''
  smsCode.value = ''
  loginBusy.value = false
}

function isCurrent(operation: number): boolean {
  return loginOpen.value && operation === loginGeneration
}

async function runLogin(operation: number, action: () => Promise<void>): Promise<void> {
  loginBusy.value = true
  loginError.value = ''
  try {
    await action()
  } catch (value) {
    if (isCurrent(operation)) {
      loginError.value = value instanceof Error ? value.message : t('views.accounts.failed')
    }
  } finally {
    if (isCurrent(operation)) {
      loginBusy.value = false
    }
  }
}

watch(loginMethod, value => {
  resetLogin()
  if (value === 'qr' && loginOpen.value) {
    void createQr()
  }
}, {flush: 'sync'})
watch(loginOpen, value => {
  if (!value) {
    resetLogin()
    loginTarget.value = undefined
    phone.value = ''
  }
}, {flush: 'sync'})

async function createQr(): Promise<void> {
  const operation = ++loginGeneration
  const accountId = loginTarget.value?.id
  clearTimeout(qrTimer)
  await runLogin(operation, async () => {
    const session = await AuthAPI.createQrLogin(accountId)
    if (!isCurrent(operation)) {
      return
    }
    loginSession.value = session
    const image = session.qrUrl ? await QRCode.toDataURL(session.qrUrl, {
      width: 280,
      margin: 2,
      errorCorrectionLevel: 'M'
    }) : ''
    if (!isCurrent(operation)) {
      return
    }
    qrImage.value = image
    qrTimer = setTimeout(() => {
      void pollQr(operation)
    }, 2000)
  })
}

async function pollQr(operation: number): Promise<void> {
  if (!isCurrent(operation) || !loginSession.value) {
    return
  }
  try {
    const session = await AuthAPI.pollQrLogin(loginSession.value.sessionId)
    if (!isCurrent(operation)) {
      return
    }
    loginSession.value = session
    if (session.status === 'Complete') {
      await refreshAccounts(() => isCurrent(operation))
      return
    }
    if (!['Expired', 'Failed'].includes(session.status)) {
      qrTimer = setTimeout(() => {
        void pollQr(operation)
      }, 2000)
    }
  } catch (value) {
    if (isCurrent(operation)) {
      loginError.value = value instanceof Error ? value.message : t('views.accounts.pollFailed')
    }
  }
}

async function submitSms(): Promise<void> {
  if (loginBusy.value) {
    return
  }
  const operation = ++loginGeneration
  const previous = loginSession.value
  const accountId = loginTarget.value?.id
  await runLogin(operation, async () => {
    const session = previous?.status === 'SmsSent'
      ? await AuthAPI.completeSms(previous.sessionId, smsCode.value)
      : await AuthAPI.createSmsLogin(countryCode.value, phone.value, accountId)
    if (!isCurrent(operation)) {
      return
    }
    loginSession.value = session
    if (session.status === 'Complete') {
      await refreshAccounts(() => isCurrent(operation))
    }
  })
}

async function sendSms(answer: SmsChallengeAnswer): Promise<void> {
  if (!loginSession.value || loginBusy.value) {
    return
  }
  const operation = ++loginGeneration
  const sessionId = loginSession.value.sessionId
  await runLogin(operation, async () => {
    const session = await AuthAPI.sendSms(sessionId, answer)
    if (isCurrent(operation)) {
      loginSession.value = session
    }
  })
}

async function setDefault(accountId: string | null): Promise<void> {
  if (busy.value) {
    return
  }
  await run(async () => {
    const updated = await AuthAPI.setDefaultBilibiliAccount(accountId)
    accountsGeneration++
    accounts.value = updated
  })
}

async function removeAccount(): Promise<void> {
  const account = selected.value
  if (!account || busy.value) {
    return
  }
  await run(async () => {
    await AuthAPI.deleteBilibiliAccount(account.id);
    selected.value = undefined;
    await refreshAccounts()
  })
}

onBeforeUnmount(() => {
  resetLogin()
  accountsGeneration++
})
</script>

<style scoped>
.accounts-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(min(100%, 340px), 1fr));
  gap: 20px;
}

.qr-login {
  display: flex;
  flex-direction: column;
  align-items: center;
  min-height: 280px;
}

.qr-login img {
  border-radius: 12px;
  background: white;
}
</style>
