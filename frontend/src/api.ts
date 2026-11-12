// Types mirror the ASP.NET Core contracts (camelCase JSON, snake_case enum values).

export type Category = 'bug' | 'risk' | 'missing_test' | 'style'
export type Severity = 'critical' | 'high' | 'medium' | 'low' | 'info'
export type Verdict = 'approve' | 'comment' | 'request_changes'

export interface Finding {
  category: Category
  severity: Severity
  confidence: 'high' | 'medium' | 'low'
  file: string
  line?: number
  endLine?: number
  title: string
  explanation: string
  suggestion?: string
}

export interface ToolCallRecord {
  turn: number
  name: string
  inputSummary: string
  isError: boolean
  resultChars: number
  durationMs: number
}

export interface ReviewResult {
  summary: string
  verdict: Verdict
  findings: Finding[]
  stats: { filesChanged: number; additions: number; deletions: number }
  model: string
  turns: number
  usage: { inputTokens: number; outputTokens: number; cacheReadInputTokens: number; cacheCreationInputTokens: number }
  toolCalls: ToolCallRecord[]
  warnings: string[]
  durationMs: number
}

export interface ReviewRun {
  id: string
  createdAt: string
  status: 'running' | 'completed' | 'failed'
  sourceKind: 'diff' | 'github_pr'
  sourceLabel: string
  pullRequestUrl?: string
  error?: string
  result?: ReviewResult
  /** The diff that was reviewed, used to show the code each finding cites. */
  diff: string
}

export interface ReviewSummary {
  id: string
  createdAt: string
  status: string
  sourceKind: string
  sourceLabel: string
  verdict?: Verdict
  findingCount: number
  highSeverityCount: number
  durationMs: number
}

export interface AgentEvent {
  type: 'status' | 'turn' | 'thinking' | 'message' | 'tool_call' | 'tool_result'
  turn: number
  message: string
  tool?: string
  isError?: boolean
  durationMs?: number
}

export interface Sample {
  name: string
  title: string
  description: string
}

export interface Health {
  status: string
  /** gemini | groq | openrouter | openai-compatible | anthropic */
  provider: string
  model: string
  modelConfigured: boolean
  /** The environment variable the server reads the model API key from. */
  apiKeyEnvVar: string
  gitHubTokenConfigured: boolean
  maxTurns: number
}

export interface ReviewRequest {
  diff?: string
  pullRequestUrl?: string
}

export class ApiError extends Error {
  readonly title: string
  constructor(title: string, detail?: string) {
    super(detail || title)
    this.title = title
  }
}

async function problem(res: Response): Promise<ApiError> {
  try {
    const body = await res.json()
    const validation = body.errors ? Object.values(body.errors).flat().join(' ') : ''
    return new ApiError(body.title ?? `HTTP ${res.status}`, body.detail ?? validation)
  } catch {
    return new ApiError(`HTTP ${res.status}`, res.statusText)
  }
}

async function getJson<T>(url: string): Promise<T> {
  const res = await fetch(url)
  if (!res.ok) throw await problem(res)
  return res.json() as Promise<T>
}

export const api = {
  health: () => getJson<Health>('/api/health'),
  samples: () => getJson<Sample[]>('/api/samples'),
  sample: async (name: string) => {
    const res = await fetch(`/api/samples/${encodeURIComponent(name)}`)
    if (!res.ok) throw await problem(res)
    return res.text()
  },
  history: () => getJson<ReviewSummary[]>('/api/reviews?take=30'),
  review: (id: string) => getJson<ReviewRun>(`/api/reviews/${id}`),
}

/**
 * POSTs a review and consumes the Server-Sent Events stream. EventSource can't POST a body, so this reads the
 * response stream with fetch and parses `event:` / `data:` frames by hand.
 */
export async function streamReview(
  request: ReviewRequest,
  onEvent: (e: AgentEvent) => void,
  signal: AbortSignal,
): Promise<ReviewRun> {
  const res = await fetch('/api/reviews/stream', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(request),
    signal,
  })

  // Model-validation failures (400) and rate limiting (429) come back as plain ProblemDetails, not a stream.
  if (!res.ok || !res.headers.get('content-type')?.includes('text/event-stream')) {
    throw await problem(res)
  }

  const reader = res.body!.pipeThrough(new TextDecoderStream()).getReader()
  let buffer = ''
  for (;;) {
    const { value, done } = await reader.read()
    if (done) break
    buffer += value
    let sep: number
    while ((sep = buffer.indexOf('\n\n')) >= 0) {
      const frame = buffer.slice(0, sep)
      buffer = buffer.slice(sep + 2)
      let event = 'message'
      let data = ''
      for (const line of frame.split('\n')) {
        if (line.startsWith('event: ')) event = line.slice(7)
        else if (line.startsWith('data: ')) data += line.slice(6)
      }
      if (!data) continue
      const payload = JSON.parse(data)
      if (event === 'agent') onEvent(payload as AgentEvent)
      else if (event === 'result') return payload as ReviewRun
      else if (event === 'error') throw new ApiError(payload.title, payload.detail)
    }
  }
  throw new ApiError('Connection closed', 'The server closed the stream before the review finished.')
}
