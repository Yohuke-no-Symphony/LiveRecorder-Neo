<template>
  <div>
    <div class="d-flex align-start ga-3">
      <v-text-field v-model="code" label="邮箱验证码" inputmode="numeric" autocomplete="one-time-code" required maxlength="12" />
      <v-btn variant="tonal" :disabled="!email || countdown > 0 || disabled" :loading="busy" height="48" @click="send">{{ countdown > 0 ? `${countdown}s` : '发送验证码' }}</v-btn>
    </div>
    <v-alert v-if="error" type="error" class="mt-3">{{ error }}</v-alert>
  </div>
</template>

<script setup lang="ts">
import { onBeforeUnmount, ref } from 'vue'
import { AuthAPI } from '@/apis/AuthAPI'
import { useAction } from '@/composables/useAction'

const code = defineModel<string>({ required: true })
const props = defineProps<{ email: string; disabled?: boolean }>()
const { busy, error, run } = useAction()
const countdown = ref(0)
let timer: ReturnType<typeof setInterval> | undefined

async function send(): Promise<void>
{
  await run(async () =>
  {
    await AuthAPI.emailCode(props.email)
    countdown.value = 60
    clearInterval(timer)
    timer = setInterval(() =>
    {
      countdown.value--
      if (countdown.value <= 0)
      {
        clearInterval(timer)
      }
    }, 1000)
  })
}
onBeforeUnmount(() => { clearInterval(timer) })
</script>
