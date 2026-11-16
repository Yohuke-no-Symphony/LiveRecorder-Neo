import type { MaybeRefOrGetter } from 'vue'
import type { AiJobPurpose } from './aiAnalysis'

export interface AiAnalysisOptions
{
  purpose?: AiJobPurpose
  active?: MaybeRefOrGetter<boolean>
  canGenerate?: MaybeRefOrGetter<boolean>
  scope?: MaybeRefOrGetter<string>
  retainSnapshot?: boolean
}
