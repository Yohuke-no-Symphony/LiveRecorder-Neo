// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { mount, type VueWrapper } from '@vue/test-utils'
import { nextTick } from 'vue'
import SessionExpiry from '../src/components/library/SessionExpiry.vue'
import type { BroadcastSession } from '../src/models/media'
import { i18n } from '../src/plugins/i18n'
import { formatDate } from '../src/utilities/format'

const mounted: VueWrapper[] = []
const expiresAtUtc = '2026-11-20T01:00:00Z'

function open(session: Pick<BroadcastSession, 'isPermanent' | 'expiresAtUtc'>): VueWrapper
{
  const wrapper = mount(SessionExpiry, { props: { session }, global: { plugins: [i18n], stubs: { VIcon: true } } })
  mounted.push(wrapper)
  return wrapper
}

beforeEach(() => { i18n.global.locale.value = 'zh-CN' })
afterEach(() =>
{
  mounted.splice(0).forEach(wrapper => wrapper.unmount())
  i18n.global.locale.value = 'zh-CN'
})

describe('录像到期时间', () =>
{
  it('三语切换本地日期文案，同时保留完整的机器可读期限', async () =>
  {
    const wrapper = open({ isPermanent: false, expiresAtUtc })
    for (const [locale, label] of [['zh-CN', '到期'], ['en-US', 'Expires'], ['ja-JP', '有効期限']] as const)
    {
      i18n.global.locale.value = locale
      await nextTick()
      const time = wrapper.get('time')
      expect(time.attributes('datetime')).toBe(expiresAtUtc)
      expect(time.text()).toBe(`${label} ${formatDate(expiresAtUtc)}`)
    }
  })

  it.each([null, undefined])('没有自动期限时不编造到期时间（%s）', value =>
  {
    const wrapper = open({ isPermanent: false, expiresAtUtc: value })
    expect(wrapper.find('time').exists()).toBe(false)
    expect(wrapper.text()).toBe('')
  })

  it('永久保留隐藏日期，解除后立即显示服务端更新的期限', async () =>
  {
    const wrapper = open({ isPermanent: false, expiresAtUtc })
    expect(wrapper.get('time').attributes('datetime')).toBe(expiresAtUtc)
    await wrapper.setProps({ session: { isPermanent: true, expiresAtUtc } })
    expect(wrapper.find('time').exists()).toBe(false)
    const restoredExpiry = '2026-12-01T01:00:00Z'
    await wrapper.setProps({ session: { isPermanent: false, expiresAtUtc: restoredExpiry } })
    expect(wrapper.get('time').attributes('datetime')).toBe(restoredExpiry)
  })
})
