// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { nextTick } from 'vue'
import { i18n } from '../src/plugins/i18n'
import { formatBytes, formatDate, formatDuration, formatMoney, formatNumber } from '../src/utilities/format'
import ConfirmDialog from '../src/components/common/ConfirmDialog.vue'
import StatusChip from '../src/components/common/StatusChip.vue'
import EventMessageRow from '../src/components/events/EventMessageRow.vue'
import CaptchaChallenge from '../src/components/accounts/CaptchaChallenge.vue'
import DataDirectoryInfo from '../src/components/common/DataDirectoryInfo.vue'
import type { RecordedEvent } from '../src/models/media'

const mounted: VueWrapper[] = []
const container = { template: '<div><slot /></div>' }

beforeEach(() => { i18n.global.locale.value = 'zh-CN' })
afterEach(() =>
{
  mounted.splice(0).forEach(wrapper => wrapper.unmount())
  i18n.global.locale.value = 'zh-CN'
  vi.unstubAllGlobals()
})

describe('运行时界面语言', () =>
{
  it('数据目录组件切换三语标签，同时保留服务器路径', async () =>
  {
    const wrapper = mount(DataDirectoryInfo, {
      props: { dataDirectory: '/srv/Data', recordsDirectory: '/srv/Data/records' },
      global: { plugins: [i18n], stubs: { VTextField: { props: ['modelValue', 'label'], template: '<label>{{ label }}<input :value="modelValue" readonly /></label>' } } },
    })
    mounted.push(wrapper)
    for (const [language, dataLabel, recordsLabel] of [['zh-CN', '数据目录', '录像目录'], ['en-US', 'Data directory', 'Recordings directory'], ['ja-JP', 'データディレクトリ', '録画ディレクトリ']] as const)
    {
      i18n.global.locale.value = language
      await nextTick()
      expect(wrapper.text()).toContain(dataLabel)
      expect(wrapper.text()).toContain(recordsLabel)
      expect(wrapper.findAll('input').map(input => input.element.value)).toEqual(['/srv/Data', '/srv/Data/records'])
    }
  })

  it('已打开的确认对话框和状态在切换语言后立即更新', async () =>
  {
    const confirm = mount(ConfirmDialog, {
      props: { modelValue: true, title: '原始标题' },
      global: { plugins: [i18n], stubs: { VDialog: container, VCard: container, VAlert: container, VBtn: { template: '<button><slot /></button>' } } },
    })
    const status = mount(StatusChip, { props: { status: 'Recording' }, global: { plugins: [i18n], stubs: { VChip: container } } })
    mounted.push(confirm, status)
    expect(confirm.text()).toContain('确认')
    expect(status.text()).toBe('录制中')

    i18n.global.locale.value = 'en-US'
    await nextTick()
    expect(confirm.text()).toContain('Cancel')
    expect(confirm.text()).toContain('Confirm')
    expect(confirm.text()).toContain('原始标题')
    expect(status.text()).toBe('Recording')

    i18n.global.locale.value = 'ja-JP'
    await nextTick()
    expect(confirm.text()).toContain('キャンセル')
    expect(confirm.text()).toContain('確認')
    expect(status.text()).toBe('録画中')
  })

  it('显式传入的确认文案始终保持调用方的值', async () =>
  {
    const wrapper = mount(ConfirmDialog, {
      props: { modelValue: true, title: '名称', actionLabel: '自定义操作' },
      global: { plugins: [i18n], stubs: { VDialog: container, VCard: container, VAlert: container, VBtn: container } },
    })
    mounted.push(wrapper)
    i18n.global.locale.value = 'en-US'
    await nextTick()
    expect(wrapper.text()).toContain('自定义操作')
    expect(wrapper.text()).not.toContain('Confirm')
  })

  it('只翻译消息身份和回复标签，保留昵称、粉丝牌和弹幕正文', async () =>
  {
    const event: RecordedEvent = {
      schemaVersion: 1, sequence: 1, eventId: 'localized-event', kind: 'Danmu', command: 'DANMU_MSG',
      receivedAtUtc: '2026-11-12T00:00:01Z', offsetMs: 1000, userId: '1', userName: '原始昵称',
      text: '原始弹幕 <script>保留为文本</script>', quantity: 1,
      payload: { isRoomAdmin: true, roomGuardLevel: 2, medalName: '原始粉丝牌', replyUserName: '回复对象' },
    }
    const wrapper = mount(EventMessageRow, { props: { event, sessionId: 'localized-session' }, global: { plugins: [i18n], stubs: { VIcon: true } } })
    mounted.push(wrapper)
    i18n.global.locale.value = 'en-US'
    await nextTick()
    expect(wrapper.text()).toContain('Moderator')
    expect(wrapper.text()).toContain('Admiral')
    expect(wrapper.text()).toContain('Reply to 回复对象:')
    expect(wrapper.get('.identity-name').text()).toBe('原始昵称')
    expect(wrapper.get('.medal').text()).toBe('原始粉丝牌')
    expect(wrapper.get('.event-content').text()).toContain(event.text)
    expect(wrapper.find('script').exists()).toBe(false)

    i18n.global.locale.value = 'ja-JP'
    await nextTick()
    expect(wrapper.text()).toContain('モデレーター')
    expect(wrapper.text()).toContain('返信')
    expect(wrapper.get('.identity-name').text()).toBe('原始昵称')
    expect(wrapper.get('.medal').text()).toBe('原始粉丝牌')
    expect(wrapper.get('.event-content').text()).toContain(event.text)
    expect(event.userName).toBe('原始昵称')
  })
})

describe('第三方验证码语言', () =>
{
  it.each(['geetest', 'geetest4'] as const)('%s 使用用户界面语言初始化验证器', async type =>
  {
    const initialize = vi.fn()
    vi.stubGlobal(type === 'geetest4' ? 'initGeetest4' : 'initGeetest', initialize)
    const script = document.createElement('script')
    script.src = type === 'geetest4' ? 'https://static.geetest.com/v4/gt4.js' : 'https://static.geetest.com/static/tools/gt.js'
    script.dataset.loaded = 'true'
    document.head.append(script)
    try
    {
      const wrapper = mount(CaptchaChallenge, {
        props: { challenge: { type, token: 'challenge-token', gt: 'captcha-id', challenge: 'server-challenge' } },
        global: { plugins: [i18n], stubs: { VBtn: { template: '<button><slot /></button>' }, VAlert: container } },
      })
      mounted.push(wrapper)
      for (const [language, expectedV3, expectedV4] of [['zh-CN', 'zh-cn', 'zho'], ['en-US', 'en', 'eng'], ['ja-JP', 'ja', 'jpn']] as const)
      {
        i18n.global.locale.value = language
        await nextTick()
        await wrapper.get('button').trigger('click')
        await flushPromises()
        expect(initialize.mock.calls.at(-1)?.[0]).toMatchObject(type === 'geetest4' ? { language: expectedV4 } : { lang: expectedV3 })
      }
      expect(initialize).toHaveBeenCalledTimes(3)
    }
    finally
    {
      script.remove()
    }
  })
})

describe('本地化格式', () =>
{
  it('运行时按界面语言切换日期排列，保持本地时刻', () =>
  {
    const value = new Date(2026, 10, 12, 8, 9).toISOString()
    expect(formatDate(value)).toContain('2026/11/12')
    expect(formatDate(value)).toContain('08:09')
    i18n.global.locale.value = 'en-US'
    expect(formatDate(value)).toContain('11/12/2026')
    expect(formatDate(value)).toContain('08:09')
    i18n.global.locale.value = 'ja-JP'
    expect(formatDate(value)).toContain('2026/11/12')
    expect(formatDate(value)).toContain('08:09')
  })

  it('金额切换显示格式后仍是人民币，精度和数值不变', () =>
  {
    expect(formatMoney(1234.567)).toContain('1,234.567')
    i18n.global.locale.value = 'en-US'
    expect(formatMoney(1234.567)).toMatch(/CN¥|CNY/)
    expect(formatMoney(1234.567)).toContain('1,234.567')
    i18n.global.locale.value = 'ja-JP'
    expect(formatMoney(1234.567)).toContain('1,234.567')
    expect(formatMoney(1234.567)).not.toMatch(/[$€]/)
    expect(formatNumber(12345.678, { minimumFractionDigits: 2, maximumFractionDigits: 2 })).toBe('12,345.68')
  })

  it('语言切换不改变时间轴输入格式、字节单位及无效值回退', () =>
  {
    for (const language of ['zh-CN', 'en-US', 'ja-JP'] as const)
    {
      i18n.global.locale.value = language
      expect(formatDuration(90061000, true)).toBe('25:01:01')
      expect(formatBytes(1536)).toBe('1.5 KB')
      expect(formatBytes(-1)).toBe('—')
      expect(formatDate('not-a-date')).toBe('—')
      expect(formatDate(null)).toBe('—')
    }
  })
})
