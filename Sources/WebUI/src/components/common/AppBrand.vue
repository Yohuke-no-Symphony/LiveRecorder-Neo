<template>
  <span class="app-brand">
    <img class="app-brand-logo" src="/branding/yohuke-logo.png" alt="" width="48" height="48" />
    <strong class="app-brand-name">{{ t('core.brand.name') }}</strong>
  </span>
</template>

<script setup lang="ts">
import { useI18n } from 'vue-i18n'

const { t } = useI18n()
</script>

<style scoped>
.app-brand { display: inline-flex; align-items: center; gap: 12px; min-width: 0; }
.app-brand-logo { flex: 0 0 auto; object-fit: contain; }
.app-brand-name { font-size: 1rem; line-height: 1.35; overflow-wrap: anywhere; }
</style>
