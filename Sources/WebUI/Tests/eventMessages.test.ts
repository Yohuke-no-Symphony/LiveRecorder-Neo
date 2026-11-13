// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import type { RecordedEvent } from '../src/models/media'
import { i18n } from '../src/plugins/i18n'
import { eventAvatarSources, eventEmoticonUrl, tokenizeEventEmoticons } from '../src/utilities/eventAssets'
import EventAvatar from '../src/components/events/EventAvatar.vue'
import EventMessageRow from '../src/components/events/EventMessageRow.vue'
import InlineEmoticons from '../src/components/events/InlineEmoticons.vue'
import RedPocketContent from '../src/components/events/RedPocketContent.vue'

const mounted: VueWrapper[] = []

function event(patch: Partial<RecordedEvent> = {}): RecordedEvent
{
  return { schemaVersion: 1, sequence: 17, eventId: 'event-17', kind: 'Danmu', command: 'DANMU_MSG', receivedAtUtc: '2026-11-13T00:00:01Z', offsetMs: 65000, userId: '123456', userName: '直播用户甲', text: '你好', quantity: 1, ...patch }
}

function mountRow(value: RecordedEvent): VueWrapper
{
  const wrapper = mount(EventMessageRow, { props: { event: value, sessionId: 'session-1' }, global: { plugins: [i18n], stubs: { VIcon: true } } })
  mounted.push(wrapper)
  return wrapper
}

afterEach(() => { mounted.splice(0).forEach(wrapper => wrapper.unmount()); vi.restoreAllMocks() })

describe('可信事件图片地址', () =>
{
  it('旧记录缺少头像地址时按UID加载同源缓存，归档地址仅用作受控回退', () =>
  {
    expect(eventAvatarSources('session-1', event()).map(url => new URL(url).pathname)).toEqual(['/api/audience/123456/avatar'])
    expect(eventAvatarSources('session-1', event({ avatarUrl: 'https://i0.hdslb.com/avatar.png' })).map(url => new URL(url).pathname)).toEqual(['/api/sessions/session-1/events/17/avatar', '/api/audience/123456/avatar'])
    expect(eventAvatarSources('session-1', event({ userId: '', avatarUrl: 'https://i0.hdslb.com/avatar.png' })).map(url => new URL(url).pathname)).toEqual(['/api/sessions/session-1/events/17/avatar'])
    expect(eventAvatarSources('session-1', event({ userId: '0' }))).toEqual([])
    expect(eventAvatarSources('session-1', event({ userId: 'https://example.test/image' }))).toEqual([])
    expect(eventAvatarSources('session-1', event({ userId: '9223372036854775808' }))).toEqual([])
  })

  it('表情使用归档原始数组索引，不因长度排序或过滤危险图片而改变', () =>
  {
    const tokens = tokenizeEventEmoticons('甲[笑脸]乙[笑]丙[坏]', [{ text: '[笑]', url: 'https://cdn.example.test/a.png' }, { text: '[坏]', url: 'javascript:alert(1)' }, { text: '[笑脸]', url: 'https://cdn.example.test/b.png' }])
    expect(tokens.filter(token => token.url).map(token => token.emoticonIndex)).toEqual([2, 0])
    expect(tokens.map(token => token.text).join('')).toBe('甲[笑脸]乙[笑]丙[坏]')
    expect(new URL(eventEmoticonUrl('session /1', 17, 2)!).pathname).toBe('/api/sessions/session%20%2F1/events/17/emoticons/2')
    expect(eventEmoticonUrl('session-1', 0, 0)).toBeUndefined()
    expect(eventEmoticonUrl('session-1', 17, -1)).toBeUndefined()
  })
})

describe('消息身份与行内内容', () =>
{
  it('头像姓名成组、粉丝牌独立显示一次、时间在身份行右侧并可跳转', async () =>
  {
    const wrapper = mountRow(event({ payload: { isRoomAdmin: true, roomGuardLevel: 2, medalName: '紫桃', medalLevel: 27 } }))
    expect(new URL(wrapper.get('.event-avatar img').attributes('src')!).pathname).toBe('/api/audience/123456/avatar')
    expect(wrapper.get('.identity-name').text()).toBe('直播用户甲')
    expect(wrapper.findAll('.message-badges')).toHaveLength(1)
    expect(wrapper.findAll('.medal')).toHaveLength(1)
    expect(wrapper.get('.medal b').text()).toBe('27')
    expect(wrapper.get('.message-badges').text()).toContain('房管')
    expect(wrapper.get('.message-badges').text()).toContain('提督')
    expect(wrapper.get('.event-content').text()).toBe('你好')
    expect(wrapper.get('.event-time').text()).toBe('01:05')
    expect(wrapper.get('.event-time').element.parentElement).toBe(wrapper.get('.event-identity').element)
    await wrapper.get('.event-time').trigger('click')
    expect(wrapper.emitted('seek')).toEqual([[65000]])
  })

  it.each(['Danmu', 'SuperChat', 'Gift', 'Guard', 'RedPocket'])('%s 使用相同身份组件而非复制标签', kind =>
  {
    const wrapper = mountRow(event({ kind, payload: { roomGuardLevel: 3, medalName: '粉丝团', medalLevel: 12 } }))
    expect(wrapper.findAll('.event-identity')).toHaveLength(1)
    expect(wrapper.findAll('.medal')).toHaveLength(1)
    expect(wrapper.get('.message-badges').text()).toContain('舰长')
  })

  it('没有身份字段时不编造粉丝牌、舰长或房管，Gap不显示访客头像', () =>
  {
    const normal = mountRow(event({ userId: '', userName: '', payload: null }))
    expect(normal.get('.identity-name').text()).toBe(i18n.global.t('components.events.guest'))
    expect(normal.find('.message-badges').exists()).toBe(false)
    expect(normal.find('img').exists()).toBe(false)
    const gap = mountRow(event({ kind: 'Gap', text: '录制中断' }))
    expect(gap.find('.event-identity').exists()).toBe(false)
    expect(gap.find('.event-avatar').exists()).toBe(false)
    expect(gap.get('.gap-message').text()).toContain('录制中断')
  })

  it('头像失败后尝试归档同源图片，仍失败时显示姓名首字，不残留破损图片', async () =>
  {
    const wrapper = mount(EventAvatar, { props: { name: '用户甲', sources: ['http://localhost/api/audience/123/avatar', 'http://localhost/api/sessions/s1/events/1/avatar'] } })
    mounted.push(wrapper)
    await wrapper.get('img').trigger('error')
    expect(wrapper.get('img').attributes('src')).toBe('http://localhost/api/sessions/s1/events/1/avatar')
    await wrapper.get('img').trigger('error')
    expect(wrapper.find('img').exists()).toBe(false)
    expect(wrapper.text()).toBe('用')
    await wrapper.setProps({ sources: ['http://localhost/api/audience/456/avatar'] })
    expect(wrapper.get('img').attributes('src')).toBe('http://localhost/api/audience/456/avatar')
  })

  it('文字与表情连续行内显示，图片失败后恢复该表情原文，拒绝脚本图片', async () =>
  {
    const wrapper = mount(InlineEmoticons, { props: { content: '<b>甲</b>[笑脸]乙[笑]丙[坏]', emoticons: [{ text: '[笑]', url: 'https://cdn.example.test/a.png' }, { text: '[坏]', url: 'javascript:alert(1)' }, { text: '[笑脸]', url: 'https://cdn.example.test/b.png' }], sessionId: 's1', sequence: 17 } })
    mounted.push(wrapper)
    expect(wrapper.element.tagName).toBe('SPAN')
    expect(wrapper.find('b').exists()).toBe(false)
    expect(wrapper.findAll('img').map(image => new URL(image.attributes('src')!).pathname)).toEqual(['/api/sessions/s1/events/17/emoticons/2', '/api/sessions/s1/events/17/emoticons/0'])
    expect(wrapper.text()).toContain('<b>甲</b>')
    expect(wrapper.text()).toContain('丙[坏]')
    await wrapper.findAll('img')[0]!.trigger('error')
    await flushPromises()
    expect(wrapper.findAll('img')).toHaveLength(1)
    expect(wrapper.text()).toContain('[笑脸]乙')
    await wrapper.get('img').trigger('error')
    expect(wrapper.find('img').exists()).toBe(false)
    expect(wrapper.text()).toBe('<b>甲</b>[笑脸]乙[笑]丙[坏]')
  })

  it('红包奖品图片失败后保留奖品文字，中奖头像使用同源UID并可降级', async () =>
  {
    const wrapper = mount(RedPocketContent, { props: { event: event({ kind: 'RedPocket', payload: { isDrawn: true, redPocket: { hasResult: true, awards: [{ giftId: '1', name: '小电视', imageUrl: 'https://cdn.example.test/gift.png' }], winners: [{ uid: 20, userName: '中奖用户', giftId: '1', giftName: '小电视' }] } } }) }, global: { plugins: [i18n] } })
    mounted.push(wrapper)
    expect(wrapper.get('.pocket-award img').attributes('referrerpolicy')).toBe('no-referrer')
    expect(new URL(wrapper.get('.pocket-winner img').attributes('src')!).pathname).toBe('/api/audience/20/avatar')
    await wrapper.get('.pocket-award img').trigger('error')
    expect(wrapper.find('.pocket-award img').exists()).toBe(false)
    expect(wrapper.get('.pocket-award').text()).toContain('小电视')
    await wrapper.get('.pocket-winner img').trigger('error')
    expect(wrapper.find('.pocket-winner img').exists()).toBe(false)
    expect(wrapper.get('.pocket-winner').text()).toContain('中奖用户')
    expect(wrapper.get('.pocket-winner .avatar-initial').text()).toBe('中')
  })

  it('没有归档上下文时保持纯正文，SC也支持同一套行内表情', () =>
  {
    const source = { content: '欢迎[笑]', emoticons: [{ text: '[笑]', url: 'https://cdn.example.test/a.png' }] }
    const withoutSession = mount(InlineEmoticons, { props: { content: source.content, emoticons: source.emoticons } })
    mounted.push(withoutSession)
    expect(withoutSession.text()).toBe('欢迎[笑]')
    expect(withoutSession.find('img').exists()).toBe(false)
    const wrapper = mountRow(event({ kind: 'SuperChat', amount: 30, payload: source }))
    expect(wrapper.get('.sc-price').text()).toContain('30.00')
    expect(new URL(wrapper.get('.sc-text img').attributes('src')!).pathname).toBe('/api/sessions/session-1/events/17/emoticons/0')
  })
})
