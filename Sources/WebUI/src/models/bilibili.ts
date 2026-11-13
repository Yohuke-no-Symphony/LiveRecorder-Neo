export type BilibiliRoomSearchSource = 'nickname' | 'uid' | 'roomId' | 'following'

export interface BilibiliRoomSearchResult
{
  roomId: string
  shortRoomId: string
  ownerUid: string
  ownerName: string
  title: string
  isLive: boolean
  sources: BilibiliRoomSearchSource[]
}

export interface BilibiliRoomSearchIssue
{
  sources: BilibiliRoomSearchSource[]
  apiCode: number | null
  code: 'followingLoginRequired' | 'invalidIdentifier' | 'incompleteResponse' | 'unavailable' | null
  message: string
}

export interface BilibiliRoomSearchPage
{
  results: BilibiliRoomSearchResult[]
  issues: BilibiliRoomSearchIssue[]
  page: number
  hasMore: boolean
}
