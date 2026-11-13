export type SetupMode = 'New' | 'Restore'

export interface SetupStatus
{
  installed: boolean
  requiresToken: boolean
  dataDirectory?: string
  recordsDirectory?: string
}

export interface SetupRequest
{
  mode: SetupMode
  token: string
  userName: string
  email: string
  password: string
  dataDirectory: string
  recordsDirectory: string
  mongoConnectionString: string
  databaseName: string
  ffmpegPath?: string
  ffprobePath?: string
}
