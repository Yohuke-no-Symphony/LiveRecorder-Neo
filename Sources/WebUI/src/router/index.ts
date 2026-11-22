import { createRouter, createWebHistory } from 'vue-router'
import { useAuthStore } from '@/stores/auth'
import { PERMISSIONS } from '@/models/auth'
import { canAccessRoute } from './access'

const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/setup', component: () => import('@/views/SetupView.vue'), meta: { public: true } },
    { path: '/login', component: () => import('@/views/LoginView.vue'), meta: { public: true } },
    { path: '/forgot-password', component: () => import('@/views/ForgotPasswordView.vue'), meta: { public: true } },
    { path: '/reset-password', component: () => import('@/views/ResetPasswordView.vue'), meta: { public: true } },
    { path: '/register', component: () => import('@/views/RegisterView.vue'), meta: { public: true } },
    { path: '/oauth/callback/:providerId', component: () => import('@/views/OAuthCallbackView.vue'), meta: { public: true } },
    { path: '/access', component: () => import('@/views/AccessView.vue'), meta: { public: true } },
    { path: '/', component: () => import('@/views/LibraryView.vue'), meta: { permissions: [PERMISSIONS.viewRecordings], breadcrumbs: [{ titleKey: 'shell.nav.library' }] } },
    { path: '/streamers/:id', component: () => import('@/views/StreamerView.vue'), meta: { permissions: [PERMISSIONS.viewRecordings], breadcrumbs: [{ titleKey: 'shell.nav.library', to: '/' }, { titleKey: 'shell.nav.streamerRecordings' }] } },
    { path: '/sessions/:id', component: () => import('@/views/SessionView.vue'), meta: { permissions: [PERMISSIONS.viewRecordings], breadcrumbs: [{ titleKey: 'shell.nav.library', to: '/' }, { titleKey: 'shell.nav.playRecording' }] } },
    { path: '/clips', component: () => import('@/views/ClipsView.vue'), meta: { breadcrumbs: [{ titleKey: 'shell.nav.clips' }] } },
    { path: '/shares/clips/:id/:token', name: 'shared-clip', component: () => import('@/views/ClipShareView.vue'), meta: { permissions: [PERMISSIONS.viewRecordings], breadcrumbs: [{ titleKey: 'shell.nav.sharedClip' }] } },
    { path: '/jobs', component: () => import('@/views/JobsView.vue'), meta: { breadcrumbs: [{ titleKey: 'shell.nav.jobs' }] } },
    { path: '/trash', component: () => import('@/views/TrashView.vue'), meta: { breadcrumbs: [{ titleKey: 'shell.nav.trash' }] } },
    { path: '/analytics', component: () => import('@/views/AnalyticsView.vue'), meta: { permissions: [PERMISSIONS.viewRecordings, PERMISSIONS.viewAnalytics], breadcrumbs: [{ titleKey: 'shell.nav.analytics' }] } },
    { path: '/account', component: () => import('@/views/AccountView.vue'), meta: { breadcrumbs: [{ titleKey: 'shell.nav.account' }] } },
    { path: '/admin/targets', component: () => import('@/views/TargetsView.vue'), meta: { admin: true, breadcrumbs: [{ titleKey: 'shell.nav.management' }, { titleKey: 'shell.nav.targets' }] } },
    { path: '/admin/accounts', component: () => import('@/views/AccountsView.vue'), meta: { admin: true, breadcrumbs: [{ titleKey: 'shell.nav.management' }, { titleKey: 'shell.nav.accounts' }] } },
    { path: '/admin/users', component: () => import('@/views/UsersView.vue'), meta: { admin: true, breadcrumbs: [{ titleKey: 'shell.nav.management' }, { titleKey: 'shell.nav.users' }] } },
    { path: '/admin/clips', component: () => import('@/views/ClipManagementView.vue'), meta: { permissions: [PERMISSIONS.manageClips], breadcrumbs: [{ titleKey: 'shell.nav.management' }, { titleKey: 'shell.nav.clipManagement' }] } },
    { path: '/admin/trash', component: () => import('@/views/TrashView.vue'), props: { management: true }, meta: { anyPermission: [PERMISSIONS.manageClips, PERMISSIONS.manageRecordings, PERMISSIONS.purgeRecordings], breadcrumbs: [{ titleKey: 'shell.nav.management' }, { titleKey: 'shell.nav.trashManagement' }] } },
    { path: '/admin/storage', component: () => import('@/views/StorageView.vue'), meta: { admin: true, breadcrumbs: [{ titleKey: 'shell.nav.management' }, { titleKey: 'shell.nav.storage' }] } },
    { path: '/admin/workers', component: () => import('@/views/WorkersView.vue'), meta: { admin: true, breadcrumbs: [{ titleKey: 'shell.nav.management' }, { titleKey: 'shell.nav.workers' }] } },
    { path: '/admin/worker-tasks', component: () => import('@/views/WorkerTasksView.vue'), meta: { admin: true, breadcrumbs: [{ titleKey: 'shell.nav.management' }, { titleKey: 'shell.nav.workerTasks' }] } },
    { path: '/admin/settings', component: () => import('@/views/SettingsView.vue'), meta: { admin: true, breadcrumbs: [{ titleKey: 'shell.nav.management' }, { titleKey: 'shell.nav.settings' }] } },
    { path: '/:pathMatch(.*)*', redirect: '/' },
  ],
  scrollBehavior: () => ({ top: 0 }),
})

router.beforeEach(async to =>
{
  const auth = useAuthStore()
  await auth.bootstrap()
  if (auth.user)
  {
    await auth.refresh()
  }
  if (!auth.installed && to.path !== '/setup')
  {
    return '/setup'
  }
  if (auth.installed && to.path === '/setup')
  {
    return auth.hasAccess ? auth.homePath : '/login'
  }
  if (!to.meta.public && !auth.user)
  {
    return { path: '/login', query: { returnTo: to.fullPath } }
  }
  if (!to.meta.public && !auth.hasAccess)
  {
    return '/access'
  }
  if (!canAccessRoute(to.meta, auth))
  {
    return auth.homePath
  }
  if (to.path === '/login' && auth.user)
  {
    return auth.hasAccess ? auth.homePath : '/access'
  }
})

export default router
