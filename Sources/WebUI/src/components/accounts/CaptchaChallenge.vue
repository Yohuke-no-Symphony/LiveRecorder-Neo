<template>
  <div>
    <template v-if="challenge.type === 'img'"><v-img :src="challenge.imageUrl" height="80" contain class="mb-4" /><v-text-field v-model="imageCode" :label="t('components.captcha.imageCode')" required autocomplete="off" /><v-btn color="primary" block class="mt-4" :disabled="!imageCode" @click="emit('verified', { token: challenge.token, captcha: imageCode })">{{ t('components.captcha.verifyAndSend') }}</v-btn></template>
    <v-btn v-else color="primary" block :loading="busy" @click="verify">{{ t('components.captcha.verify') }}</v-btn>
    <v-alert v-if="error" type="error" class="mt-4">{{ error }}</v-alert>
  </div>
</template>

<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { onBeforeUnmount, ref } from 'vue'
import type { BilibiliChallenge, SmsChallengeAnswer } from '@/models/auth'

const { t, locale } = useI18n()

interface CaptchaInstance
{
  onReady: (callback: () => void) => CaptchaInstance
  onSuccess: (callback: () => void) => CaptchaInstance
  onError: (callback: () => void) => CaptchaInstance
  onClose?: (callback: () => void) => CaptchaInstance
  getValidate: () => Record<string, string> | false
  verify?: () => void
  showCaptcha?: () => void
  destroy?: () => void
}
type CaptchaInitializer = (options: Record<string, unknown>, callback: (instance: CaptchaInstance) => void) => void
const props = defineProps<{ challenge: BilibiliChallenge }>()
const emit = defineEmits<{ verified: [answer: SmsChallengeAnswer] }>()
const imageCode = ref('')
const busy = ref(false)
const error = ref('')
let captcha: CaptchaInstance | undefined
let disposed = false

function loadScript(url: string): Promise<void>
{
  return new Promise((resolve, reject) =>
  {
    const existing = document.querySelector<HTMLScriptElement>(`script[src="${url}"]`)
    if (existing?.dataset.loaded === 'true') { resolve(); return }
    const script = existing ?? document.createElement('script')
    script.addEventListener('load', () => { script.dataset.loaded = 'true'; resolve() }, { once: true })
    script.addEventListener('error', () => { script.remove(); reject(new Error(t('components.captcha.loadFailed'))) }, { once: true })
    if (!existing)
    {
      script.src = url
      script.async = true
      document.head.append(script)
    }
  })
}
async function verify(): Promise<void>
{
  busy.value = true
  error.value = ''
  try
  {
    const isV4 = props.challenge.type === 'geetest4'
    const global = window as Window & { initGeetest?: CaptchaInitializer; initGeetest4?: CaptchaInitializer }
    await loadScript(isV4 ? 'https://static.geetest.com/v4/gt4.js' : 'https://static.geetest.com/static/tools/gt.js')
    if (disposed) { return }
    const initialize = isV4 ? global.initGeetest4 : global.initGeetest
    if (!initialize) { throw new Error(t('components.captcha.unavailable')) }
    captcha?.destroy?.()
    const language = locale.value.startsWith('en') ? 'eng' : locale.value.startsWith('ja') ? 'jpn' : 'zho'
    const lang = locale.value.startsWith('en') ? 'en' : locale.value.startsWith('ja') ? 'ja' : 'zh-cn'
    initialize(isV4
      ? { captchaId: props.challenge.gt, product: 'bind', language, protocol: 'https://' }
      : { gt: props.challenge.gt, challenge: props.challenge.challenge, product: 'bind', lang, offline: false, new_captcha: true, https: true }, instance =>
    {
      if (disposed) { instance.destroy?.(); return }
      captcha = instance
      instance.onReady(() => { busy.value = false; if (isV4) { instance.showCaptcha?.() } else { instance.verify?.() } })
      instance.onError(() => { busy.value = false; error.value = t('components.captcha.failed') })
      instance.onClose?.(() => { busy.value = false })
      instance.onSuccess(() =>
      {
        const result = instance.getValidate()
        if (!result) { return }
        emit('verified', isV4
          ? { token: props.challenge.token, lotNumber: result.lot_number, captchaOutput: result.captcha_output, passToken: result.pass_token, genTime: result.gen_time }
          : { token: props.challenge.token, challenge: result.geetest_challenge, validate: result.geetest_validate, seccode: result.geetest_seccode })
      })
    })
  }
  catch (value)
  {
    busy.value = false
    error.value = value instanceof Error ? value.message : t('components.captcha.loadFailed')
  }
}
onBeforeUnmount(() => { disposed = true; captcha?.destroy?.() })
</script>
