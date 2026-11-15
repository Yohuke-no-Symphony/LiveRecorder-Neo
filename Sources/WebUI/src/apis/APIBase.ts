import { getServerUrl } from './ServerURL'
import { i18n } from '@/plugins/i18n'
import type { DownloadedFile } from '@/models/download'
import { getDownloadFileName } from '@/utilities/downloadFile'

export class ApiError extends Error
{
  public constructor(public readonly status: number, message: string, public readonly code?: string)
  {
    super(message)
    this.name = 'ApiError'
  }
}

let csrfToken = ''

export function setCsrfToken(value: string): void
{
  csrfToken = value
}

export async function request<T>(path: string, options: RequestInit = {}): Promise<T>
{
  const response = await requestResponse(path, options, 'application/json')
  if (response.status === 204 || response.headers.get('Content-Length') === '0')
  {
    return undefined as T
  }
  return await response.json() as T
}

export async function download(path: string, fallbackFileName: string, options: RequestInit = {}): Promise<DownloadedFile>
{
  const response = await requestResponse(path, options, '*/*')
  let blob: Blob
  try
  {
    blob = await response.blob()
  }
  catch (error)
  {
    throw connectionError(error)
  }
  return { blob, fileName: getDownloadFileName(response.headers.get('Content-Disposition'), fallbackFileName) }
}

async function requestResponse(path: string, options: RequestInit, accept: string): Promise<Response>
{
  const headers = new Headers(options.headers)
  headers.set('Accept', accept)
  headers.set('Accept-Language', i18n.global.locale.value)
  if (options.body && !(options.body instanceof FormData))
  {
    headers.set('Content-Type', 'application/json')
  }
  if (csrfToken && !['GET', 'HEAD', 'OPTIONS'].includes(options.method ?? 'GET'))
  {
    headers.set('X-CSRF-TOKEN', csrfToken)
  }
  let response: Response
  try
  {
    response = await fetch(getServerUrl(path.startsWith('/api/') ? path : `/api/${path.replace(/^\//, '')}`), {
      ...options, headers, credentials: 'include',
    })
  }
  catch (error)
  {
    throw connectionError(error)
  }
  if (!response.ok)
  {
    const problem = await response.json().catch(() => ({})) as Record<string, unknown>
    const validation = problem.errors && typeof problem.errors === 'object'
      ? Object.values(problem.errors as Record<string, unknown>).flat().filter(value => typeof value === 'string').join('; ')
      : ''
    const fallbackKey = response.status === 401 ? 'core.errors.unauthorized' : response.status === 403 ? 'core.errors.forbidden' : response.status === 429 ? 'core.errors.tooManyRequests' : 'core.errors.request'
    const message = validation || [problem.detail, problem.message, problem.title, problem.error].find(value => typeof value === 'string')
    throw new ApiError(response.status, typeof message === 'string' ? message : i18n.global.t(fallbackKey, { status: response.status }), typeof problem.code === 'string' ? problem.code : undefined)
  }
  return response
}

function connectionError(error: unknown): Error
{
  return error instanceof DOMException && error.name === 'AbortError'
    ? error
    : new ApiError(0, i18n.global.t('core.errors.connection'))
}

export function post<T>(path: string, body?: unknown): Promise<T>
{
  return request<T>(path, { method: 'POST', body: body === undefined ? undefined : JSON.stringify(body) })
}

export function put<T>(path: string, body: unknown): Promise<T>
{
  return request<T>(path, { method: 'PUT', body: JSON.stringify(body) })
}

export function remove<T = void>(path: string): Promise<T>
{
  return request<T>(path, { method: 'DELETE' })
}

export function query(values: Record<string, string | number | boolean | undefined | null>): string
{
  const parameters = new URLSearchParams()
  for (const [key, value] of Object.entries(values))
  {
    if (value !== undefined && value !== null && value !== '')
    {
      parameters.set(key, String(value))
    }
  }
  const encoded = parameters.toString()
  return encoded ? `?${encoded}` : ''
}
