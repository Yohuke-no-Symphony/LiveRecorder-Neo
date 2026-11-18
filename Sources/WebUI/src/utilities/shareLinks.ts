export function absoluteShareLink(path: string): string
{
  return new URL(path, window.location.origin).href
}

export async function copyShareLink(link: string): Promise<boolean>
{
  try
  {
    await navigator.clipboard.writeText(link)
    return true
  }
  catch
  {
    return false
  }
}
