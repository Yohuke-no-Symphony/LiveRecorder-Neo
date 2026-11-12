import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { getServerUrl } from '../src/apis/ServerURL'
import { MediaAPI } from '../src/apis/MediaAPI'

beforeEach(() =>
{
  vi.stubGlobal('window', { location: { origin: 'https://library.example:8443' } })
})

afterEach(() =>
{
  vi.unstubAllEnvs()
  vi.unstubAllGlobals()
})

describe('服务器地址', () =>
{
  it('开发环境直接访问指定后端端口', () =>
  {
    vi.stubEnv('DEV', true)
    vi.stubEnv('VITE_API_BASE_URL', 'http://127.0.0.1:4784/')
    expect(getServerUrl('/api/auth/me')).toBe('http://127.0.0.1:4784/api/auth/me')
    expect(getServerUrl('api/sessions?take=20')).toBe('http://127.0.0.1:4784/api/sessions?take=20')
    expect(getServerUrl('/hubs/status')).toBe('http://127.0.0.1:4784/hubs/status')
  })

  it('开发地址未配置时使用当前站点', () =>
  {
    vi.stubEnv('DEV', true)
    vi.stubEnv('VITE_API_BASE_URL', '')
    expect(getServerUrl('/api/auth/me')).toBe('https://library.example:8443/api/auth/me')
  })

  it('发布环境始终使用当前站点的协议、域名与端口', () =>
  {
    vi.stubEnv('DEV', false)
    vi.stubEnv('VITE_API_BASE_URL', 'http://127.0.0.1:4784')
    expect(getServerUrl('/api/auth/me')).toBe('https://library.example:8443/api/auth/me')
  })

  it('媒体资源与下载共用开发后端，保留资源标识编码', () =>
  {
    vi.stubEnv('DEV', true)
    vi.stubEnv('VITE_API_BASE_URL', 'http://127.0.0.1:4884')
    expect(MediaAPI.avatar('主播/1')).toBe('http://127.0.0.1:4884/api/streamers/%E4%B8%BB%E6%92%AD%2F1/avatar')
    expect(MediaAPI.cover('session')).toBe('http://127.0.0.1:4884/api/sessions/session/cover')
    expect(MediaAPI.previewClip('clip')).toBe('http://127.0.0.1:4884/api/clips/clip/media')
    expect(MediaAPI.downloadClip('clip')).toBe('http://127.0.0.1:4884/api/clips/clip/download')
    expect(MediaAPI.downloadJobOutput('job', 1)).toBe('http://127.0.0.1:4884/api/jobs/job/outputs/1')
    expect(getServerUrl('/api/playback/key/generation/index.m3u8')).toBe('http://127.0.0.1:4884/api/playback/key/generation/index.m3u8')
  })
})
