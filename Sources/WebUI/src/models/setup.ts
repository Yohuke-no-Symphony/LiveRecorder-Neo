export type SetupMode = 'New' | 'Restore'

export interface SetupStatus
{
  installed: boolean
  requiresToken: boolean
  dataDirectory?: string
  recordsDirectory?: string
}

export interface SetupConfiguration
{
  token: string
  dataDirectory: string
  recordsDirectory: string
  mongoConnectionString: string
  databaseName: string
  ffmpegPath?: string
  ffprobePath?: string
}

export type SetupRequest = SetupConfiguration & (
  { mode: 'New'; userName: string; email: string; password: string }
  | { mode: 'Restore' }
)
