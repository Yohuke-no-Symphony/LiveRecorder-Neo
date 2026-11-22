<template>
  <div class="auth-page">
    <v-card class="setup-card">
      <AppBrand/>
      <h1 class="auth-title">{{ t('views.setup.title') }}</h1>
      <div class="d-flex flex-wrap ga-3 mb-8">
        <v-chip v-for="(name, index) in steps" :key="name" :color="step === index ? 'primary' : undefined">{{
          t('views.setup.step', { number: index + 1, name: t(`views.setup.${name}Step`) }) }}</v-chip>
      </div>
      <form class="form-stack" @submit.prevent="next">
        <template v-if="currentStep === 'mode'">
          <v-select v-model="form.mode" :items="setupModes" :label="t('views.setup.mode')"/>
          <p class="text-body-2 muted">{{ restoring ? t('views.setup.restoreDescription') :
            t('views.setup.newDescription') }}</p>
          <v-text-field v-if="auth.requiresToken" v-model="form.token" :label="t('views.setup.token')" type="password"
                        required autocomplete="off"/>
        </template>
        <template v-else-if="currentStep === 'admin'">
          <v-text-field v-model="administrator.userName" :label="t('views.setup.adminName')" required
                        autocomplete="nickname"/>
          <v-text-field v-model="administrator.email" :label="t('views.setup.adminEmail')" type="email" required
                        autocomplete="email"/>
          <v-text-field v-model="administrator.password" :label="t('views.common.password')" type="password" required
                        minlength="12" maxlength="256" autocomplete="new-password"/>
          <v-text-field v-model="confirmation" :label="t('views.setup.confirmPassword')" type="password" required
                        autocomplete="new-password"
                        :error-messages="confirmation && confirmation !== administrator.password ? t('views.setup.passwordMismatch') : ''"/>
        </template>
        <template v-else-if="currentStep === 'storage'">
          <DataDirectoryInfo v-model:data-directory="form.dataDirectory"
                             v-model:records-directory="form.recordsDirectory" editable/>
          <v-text-field v-model="mediaTools.ffmpegPath" label="FFmpeg" :required="!restoring"/>
          <v-text-field v-model="mediaTools.ffprobePath" label="FFprobe" :required="!restoring"/>
          <p v-if="restoring" class="text-body-2 muted">{{ t('views.setup.keepMediaTools') }}</p>
        </template>
        <template v-else>
          <v-text-field v-model="form.mongoConnectionString" :label="t('views.setup.mongoConnection')" required
                        autocomplete="off"/>
          <v-text-field v-model="form.databaseName" :label="t('views.setup.databaseName')" required/>
        </template>
        <v-alert v-if="error" type="error">{{ error }}</v-alert>
        <div class="d-flex justify-space-between ga-3 mt-3">
          <v-btn v-if="step > 0" variant="text" :disabled="busy" @click="previous">{{ t('views.setup.previous') }}</v-btn>
          <v-spacer/>
          <v-btn type="submit" color="primary" :loading="busy">{{ lastStep ? (restoring ? t('views.setup.restore') :
            t('views.setup.finish')) : t('views.setup.next') }}<v-icon v-if="!lastStep" icon="mdi-arrow-right" end/>
          </v-btn>
        </div>
      </form>
    </v-card>
  </div>
</template>

<script setup lang="ts">
import {useI18n} from 'vue-i18n'
import AppBrand from '@/components/common/AppBrand.vue'
import DataDirectoryInfo from '@/components/common/DataDirectoryInfo.vue'
import {computed, reactive, ref} from 'vue'
import {AuthAPI} from '@/apis/AuthAPI'
import {useAuthStore} from '@/stores/auth'
import {useAction} from '@/composables/useAction'
import type {SetupConfiguration, SetupMode} from '@/models/setup'

const {t} = useI18n()

const auth = useAuthStore()
const step = ref(0)
const confirmation = ref('')
const {busy, error, run} = useAction()
const form = reactive<Omit<SetupConfiguration, 'ffmpegPath' | 'ffprobePath'> & { mode: SetupMode }>({
  mode: 'New',
  token: '',
  dataDirectory: auth.dataDirectory,
  recordsDirectory: auth.recordsDirectory,
  mongoConnectionString: 'mongodb://127.0.0.1:27017',
  databaseName: 'yohuke-live-recorder'
})
const administrator = reactive({userName: '', email: '', password: ''})
const toolsByMode = reactive<Record<SetupMode, { ffmpegPath: string; ffprobePath: string }>>({
  New: {ffmpegPath: 'ffmpeg', ffprobePath: 'ffprobe'},
  Restore: {ffmpegPath: '', ffprobePath: ''},
})
const restoring = computed(() => form.mode === 'Restore')
const steps = computed(() => restoring.value ? ['mode', 'storage', 'database'] : ['mode', 'admin', 'storage', 'database'])
const currentStep = computed(() => steps.value[step.value])
const lastStep = computed(() => step.value === steps.value.length - 1)
const mediaTools = computed(() => toolsByMode[form.mode])
const setupModes = computed(() => [
  {title: t('views.setup.new'), value: 'New'},
  {title: t('views.setup.restore'), value: 'Restore'},
])

function previous(): void {
  error.value = ''
  step.value--
}

async function next(): Promise<void> {
  if (currentStep.value === 'admin' && confirmation.value !== administrator.password) {
    error.value = t('views.setup.passwordMismatch')
    return
  }
  if (!lastStep.value) {
    error.value = ''
    step.value++
    return
  }
  await run(async () => {
    const configuration = {...form, ...mediaTools.value}
    await AuthAPI.setup(restoring.value ? {...configuration, mode: 'Restore'} : {
      ...configuration,
      mode: 'New', ...administrator
    })
    window.location.assign('/login')
  })
}
</script>

<style scoped>.setup-card {
  width: min(100%, 590px);
  padding: 36px;
}</style>
