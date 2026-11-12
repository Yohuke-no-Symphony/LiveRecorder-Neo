<template>
  <PageHeader title="登录方式" />
  <v-alert v-if="error" type="error" class="mb-6">{{ error }}</v-alert>
  <v-progress-linear v-if="busy && !providers.length" indeterminate color="primary" />
  <v-card v-if="providers.length" style="max-width: 720px"><v-list bg-color="transparent" class="pa-2"><v-list-item v-for="provider in providers" :key="provider.id" :title="provider.name" prepend-icon="mdi-account-key-outline" class="py-4"><template #append><v-btn :variant="boundIds.includes(provider.id) ? 'text' : 'tonal'" :color="boundIds.includes(provider.id) ? 'success' : 'primary'" :disabled="boundIds.includes(provider.id) || !!bindingId" :loading="bindingId === provider.id" :prepend-icon="boundIds.includes(provider.id) ? 'mdi-check' : 'mdi-link-variant'" @click="bind(provider.id)">{{ boundIds.includes(provider.id) ? '已绑定' : '绑定' }}</v-btn></template></v-list-item></v-list></v-card>
  <EmptyState v-else-if="!busy" title="暂无外部登录方式" icon="mdi-account-key-outline" />
</template>

<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { AuthAPI } from '@/apis/AuthAPI'
import { useAction } from '@/composables/useAction'
import PageHeader from '@/components/common/PageHeader.vue'
import EmptyState from '@/components/common/EmptyState.vue'

const providers = ref<{ id: string; name: string }[]>([])
const boundIds = ref<string[]>([])
const bindingId = ref('')
const { busy, error, run } = useAction()
onMounted(() => { void run(async () => { [providers.value, boundIds.value] = await Promise.all([AuthAPI.providers(), AuthAPI.identities()]) }) })
async function bind(id: string): Promise<void>
{
  bindingId.value = id
  await run(async () =>
  {
    const result = await AuthAPI.oauthBind(id)
    sessionStorage.setItem('oauth_return_to', '/account')
    window.location.assign(result.authorizationUrl)
  })
  bindingId.value = ''
}
</script>
