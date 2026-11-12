export function getServerUrl(path: string): string
{
  const baseUrl = import.meta.env.DEV && import.meta.env.VITE_API_BASE_URL?.trim()
    ? import.meta.env.VITE_API_BASE_URL.trim()
    : window.location.origin
  return new URL(path, `${baseUrl.replace(/\/+$/, '')}/`).href
}
