import { createRouter, createWebHistory } from 'vue-router'
import { useAuthStore } from '@/stores/auth'

const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/setup', component: () => import('@/views/SetupView.vue'), meta: { public: true } },
    { path: '/login', component: () => import('@/views/LoginView.vue'), meta: { public: true } },
    { path: '/register', component: () => import('@/views/RegisterView.vue'), meta: { public: true } },
    { path: '/oauth/callback/:providerId', component: () => import('@/views/OAuthCallbackView.vue'), meta: { public: true } },
    { path: '/access', component: () => import('@/views/AccessView.vue'), meta: { public: true } },
    { path: '/', component: () => import('@/views/LibraryView.vue') },
    { path: '/streamers/:id', component: () => import('@/views/StreamerView.vue') },
    { path: '/sessions/:id', component: () => import('@/views/SessionView.vue') },
    { path: '/clips', component: () => import('@/views/ClipsView.vue') },
    { path: '/jobs', component: () => import('@/views/JobsView.vue') },
    { path: '/trash', component: () => import('@/views/TrashView.vue') },
    { path: '/analytics', component: () => import('@/views/AnalyticsView.vue') },
    { path: '/account', component: () => import('@/views/AccountView.vue') },
    { path: '/admin/targets', component: () => import('@/views/TargetsView.vue'), meta: { admin: true } },
    { path: '/admin/accounts', component: () => import('@/views/AccountsView.vue'), meta: { admin: true } },
    { path: '/admin/users', component: () => import('@/views/UsersView.vue'), meta: { admin: true } },
    { path: '/admin/settings', component: () => import('@/views/SettingsView.vue'), meta: { admin: true } },
    { path: '/:pathMatch(.*)*', redirect: '/' },
  ],
  scrollBehavior: () => ({ top: 0 }),
})

router.beforeEach(async to =>
{
  const auth = useAuthStore()
  await auth.bootstrap()
  if (!auth.installed && to.path !== '/setup')
  {
    return '/setup'
  }
  if (auth.installed && to.path === '/setup')
  {
    return auth.hasAccess ? '/' : '/login'
  }
  if (!to.meta.public && !auth.user)
  {
    return { path: '/login', query: { returnTo: to.fullPath } }
  }
  if (!to.meta.public && !auth.hasAccess)
  {
    return '/access'
  }
  if (to.meta.admin && !auth.isAdmin)
  {
    return '/'
  }
  if (to.path === '/login' && auth.user)
  {
    return auth.hasAccess ? '/' : '/access'
  }
})

export default router
