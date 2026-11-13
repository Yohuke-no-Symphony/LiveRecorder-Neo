// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { mount, type VueWrapper } from '@vue/test-utils'
import { nextTick } from 'vue'
import BilibiliAccountCard from '../src/components/accounts/BilibiliAccountCard.vue'
import type { BilibiliAccount } from '../src/models/auth'
import { i18n } from '../src/plugins/i18n'
import { formatDate } from '../src/utilities/format'

const mounted: VueWrapper[] = []
const container = { template: '<div><slot /></div>' }
const account: BilibiliAccount = {
  id: 'account-1', uid: 21107642, userName: 'Crestruction–樱游社',
  avatarUrl: 'https://i0.hdslb.com/account.jpg', status: 'Valid', lastValidatedAtUtc: '2026-11-13T00:25:00Z',
}

function mountCard(patch: Partial<BilibiliAccount> = {}): VueWrapper
{
  const wrapper = mount(BilibiliAccountCard, {
    props: { account: { ...account, ...patch } },
    global: {
      plugins: [i18n],
      stubs: {
        VCard: container, VAvatar: container, VChip: container, VList: container,
        VIcon: { props: ['icon'], template: '<span :data-icon="icon" />' },
        VMenu: { template: '<div><slot name="activator" :props="{}" /><slot /></div>' },
        VBtn: { template: '<button><slot /></button>' },
        VListItem: { props: ['title'], template: '<button>{{ title }}</button>' },
      },
    },
  })
  mounted.push(wrapper)
  return wrapper
}

beforeEach(() =>
{
  i18n.global.locale.value = 'zh-CN'
  vi.stubEnv('DEV', false)
})

afterEach(() =>
{
  mounted.splice(0).forEach(wrapper => wrapper.unmount())
  i18n.global.locale.value = 'zh-CN'
  vi.unstubAllEnvs()
})

describe('B站账号卡片', () =>
{
  it('头像从本站鉴权端点加载，不直接访问远端 CDN', () =>
  {
    const wrapper = mountCard()
    const image = wrapper.get('img')
    const url = new URL(image.attributes('src')!)
    expect(url.origin).toBe(window.location.origin)
    expect(url.pathname).toBe(`/api/admin/bilibili/accounts/${account.id}/avatar`)
    expect(url.searchParams.get('v')).toBe(account.avatarUrl)
    expect(image.attributes('referrerpolicy')).toBe('no-referrer')
    expect(image.attributes('alt')).toBe('')
    expect(wrapper.get('h2').text()).toBe(account.userName)
    expect(wrapper.text()).toContain(`UID ${account.uid}`)
    expect(wrapper.text()).toContain('有效')
  })

  it('加载失败后显示占位，不反复请求失败图片', async () =>
  {
    const wrapper = mountCard()
    await wrapper.get('img').trigger('error')
    expect(wrapper.find('img').exists()).toBe(false)
    expect(wrapper.find('[data-icon="mdi-account-outline"]').exists()).toBe(true)
    expect(wrapper.get('h2').text()).toBe(account.userName)
  })

  it('失败后收到新头像地址时重新加载', async () =>
  {
    const wrapper = mountCard()
    await wrapper.get('img').trigger('error')
    const updated = { ...account, avatarUrl: 'https://i1.hdslb.com/new-account.jpg' }
    await wrapper.setProps({ account: updated })
    expect(new URL(wrapper.get('img').attributes('src')!).searchParams.get('v')).toBe(updated.avatarUrl)
  })

  it('没有头像地址时直接显示占位', () =>
  {
    const wrapper = mountCard({ avatarUrl: '' })
    expect(wrapper.find('img').exists()).toBe(false)
    expect(wrapper.find('[data-icon="mdi-account-outline"]').exists()).toBe(true)
  })

  it.each([21107642, 3546873520458421])('UID %s 完整显示，账号状态独立排列', uid =>
  {
    const wrapper = mountCard({ uid })
    expect(wrapper.get('.account-card-uid').text()).toBe(`UID ${uid}`)
    expect(wrapper.get('.account-card-identity').text()).toContain('有效')
    expect(wrapper.get('.account-card-identity').text()).not.toContain(`UID ${uid}`)
  })

  it('校验暂不可用时使用本地化状态', async () =>
  {
    const wrapper = mountCard({ status: 'Unavailable' })
    expect(wrapper.text()).toContain('暂不可用')
    i18n.global.locale.value = 'en-US'
    await nextTick()
    expect(wrapper.text()).toContain('Temporarily unavailable')
    i18n.global.locale.value = 'ja-JP'
    await nextTick()
    expect(wrapper.text()).toContain('一時的に利用不可')
  })

  it('校验时间有明确标签，缺少时间时显示尚未校验', async () =>
  {
    const wrapper = mountCard()
    expect(wrapper.text()).toContain('最近校验')
    expect(wrapper.get('time').attributes('datetime')).toBe(account.lastValidatedAtUtc)
    expect(wrapper.get('time').text()).toBe(formatDate(account.lastValidatedAtUtc))
    await wrapper.setProps({ account: { ...account, lastValidatedAtUtc: null } })
    expect(wrapper.find('time').exists()).toBe(false)
    expect(wrapper.text()).toContain('尚未校验')
  })

  it('昵称完整保留在标题中，移除操作交给原有确认流程', async () =>
  {
    const userName = '很长的账号名称'.repeat(12)
    const wrapper = mountCard({ userName })
    expect(wrapper.get('h2').attributes('title')).toBe(userName)
    expect(wrapper.get('h2').text()).toBe(userName)
    const remove = wrapper.findAll('button').find(button => button.text() === '移除账号')!
    await remove.trigger('click')
    expect(wrapper.emitted('remove')).toEqual([[{ ...account, userName }]])
  })

  it('标签和状态随界面语言切换，昵称保持原文', async () =>
  {
    const wrapper = mountCard({ lastValidatedAtUtc: null })
    for (const [language, label, missing, status] of [
      ['en-US', 'Last validated', 'Not validated yet', 'Active'],
      ['ja-JP', '最終確認', '未確認', '有効'],
    ] as const)
    {
      i18n.global.locale.value = language
      await nextTick()
      expect(wrapper.text()).toContain(label)
      expect(wrapper.text()).toContain(missing)
      expect(wrapper.text()).toContain(status)
      expect(wrapper.get('h2').text()).toBe(account.userName)
    }
  })
})
