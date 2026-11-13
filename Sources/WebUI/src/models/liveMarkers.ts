export type LiveMarkerColor = 'Red' | 'Orange' | 'Yellow' | 'Green' | 'Blue' | 'Magenta' | 'Grey'

export interface LiveMarkerFile
{
  id: string
  originalFileName: string
  uploaderId: string
  uploaderName: string
  uploadedAtUtc: string
  entryCount: number
  calibrationMs: number
  sourceTimeZoneId: string
  canManage: boolean
}

export interface LiveMarkerFileList
{
  files: LiveMarkerFile[]
  defaultTimeZoneId: string
}

export interface LiveMarkerEntry
{
  index: number
  realDateTime: string
  liveTime: string
  message: string
  markerColor: LiveMarkerColor
  offsetMs: number
}

export interface LiveMarkerDetail
{
  file: LiveMarkerFile
  startTime: string
  entries: LiveMarkerEntry[]
}

export interface LiveMarkerUpload
{
  file: File
  timeZoneId: string
  calibrationMs: number
}
