import type { DownloadedFile } from '@/models/download'

export function getDownloadFileName(contentDisposition: string | null, fallback: string): string
{
  const extended = contentDisposition?.match(/(?:^|;)\s*filename\*\s*=\s*(?:"([^"]*)"|([^;]*))/i)
  const encodedName = (extended?.[1] ?? extended?.[2])?.trim().match(/^UTF-8'[^']*'(.*)$/i)?.[1]
  if (encodedName)
  {
    try
    {
      const fileName = sanitizeFileName(decodeURIComponent(encodedName))
      if (fileName)
      {
        return fileName
      }
    }
    catch {}
  }
  const plain = contentDisposition?.match(/(?:^|;)\s*filename\s*=\s*(?:"((?:\\.|[^"\\])*)"|([^;]*))/i)
  const fileName = sanitizeFileName((plain?.[1]?.replace(/\\(.)/g, '$1') ?? plain?.[2] ?? '').trim())
  return fileName || sanitizeFileName(fallback)
}

export function saveDownloadedFile(file: DownloadedFile): void
{
  const url = URL.createObjectURL(file.blob)
  const anchor = document.createElement('a')
  try
  {
    anchor.href = url
    anchor.download = file.fileName
    anchor.hidden = true
    document.body.append(anchor)
    anchor.click()
  }
  finally
  {
    anchor.remove()
    setTimeout(() => URL.revokeObjectURL(url), 1000)
  }
}

function sanitizeFileName(value: string): string
{
  return (value.split(/[\\/]/).pop() ?? '')
    .replace(/[\u0000-\u001f\u007f\u202a-\u202e\u2066-\u2069]/g, '')
    .replace(/[<>:"|?*]/g, '_')
    .replace(/^[. ]+|[. ]+$/g, '')
}
