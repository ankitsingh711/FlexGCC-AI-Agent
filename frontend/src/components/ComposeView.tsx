import { useEffect, useMemo, useState } from 'react'
import { api, type ReviewRequest, type ReviewSummary, type Sample } from '../api'
import { diffStats, parseDiff } from '../lib/diff'
import { timeAgo } from '../lib/format'
import { Arrow, CategoryIcon, ChevronRight, DiffIcon, LinkIcon, PrIcon } from './Icons'

type Mode = 'diff' | 'pr'

interface Props {
  disabled: boolean
  history: ReviewSummary[]
  initial: ReviewRequest | null
  onSubmit: (request: ReviewRequest) => void
  onOpen: (id: string) => void
}

const MAX_DIFF_CHARS = 400_000
const PR_URL = /^https:\/\/github\.com\/[\w.-]+\/[\w.-]+\/pull\/\d+(?:[/?#].*)?$/i
const isMac = typeof navigator !== 'undefined' && /Mac|iPhone|iPad/.test(navigator.platform)

const CHECKS = [
  { id: 'bug', title: 'Bugs', text: 'Logic errors, null handling, broken error paths, wrong conditions.' },
  { id: 'risk', title: 'Risks', text: 'Security, data loss, breaking changes, performance cliffs.' },
  { id: 'missing_test', title: 'Missing tests', text: 'Changed behaviour and edge cases with no coverage.' },
  { id: 'style', title: 'Style', text: 'Readability issues that matter — not linter noise.' },
]

export function ComposeView({ disabled, history, initial, onSubmit, onOpen }: Props) {
  const [mode, setMode] = useState<Mode>(initial?.pullRequestUrl ? 'pr' : 'diff')
  const [diff, setDiff] = useState(initial?.diff ?? '')
  const [url, setUrl] = useState(initial?.pullRequestUrl ?? '')
  const [samples, setSamples] = useState<Sample[]>([])
  const [activeSample, setActiveSample] = useState<string | null>(null)
  const [sampleError, setSampleError] = useState(false)

  useEffect(() => {
    api.samples().then(setSamples).catch(() => setSampleError(true))
  }, [])

  const stats = useMemo(() => diffStats(parseDiff(diff)), [diff])

  const loadSample = async (s: Sample) => {
    try {
      setDiff(await api.sample(s.name))
      setActiveSample(s.name)
      setMode('diff')
    } catch {
      setSampleError(true)
    }
  }

  // Client-side checks mirror the server's; the server stays the source of truth.
  const error =
    mode === 'diff'
      ? diff.length > MAX_DIFF_CHARS
        ? `This diff is ${diff.length.toLocaleString()} characters; the limit is ${MAX_DIFF_CHARS.toLocaleString()}.`
        : diff.trim() && stats.files === 0
          ? 'This doesn’t look like a unified diff — no file headers or hunks found.'
          : null
      : url.trim() && !PR_URL.test(url.trim())
        ? 'Expected https://github.com/{owner}/{repo}/pull/{number}'
        : null
  const ready = !disabled && !error && (mode === 'diff' ? diff.trim().length > 0 : url.trim().length > 0)

  const submit = () => {
    if (ready) onSubmit(mode === 'diff' ? { diff } : { pullRequestUrl: url.trim() })
  }

  const onKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'Enter' && (e.metaKey || e.ctrlKey)) {
      e.preventDefault()
      submit()
    }
  }

  return (
    <div className="layout">
      <main className="layout-main stack-20">
        <div className="rise">
          <div className="eyebrow">New review</div>
          <h1 className="display">Review a change</h1>
          <p className="lede">
            Paste a unified diff or point at a public GitHub pull request. The agent investigates with read-only tools, then
            reports bugs, risks, missing tests and style issues — each tied to a file and line.
          </p>
        </div>

        <form className="panel compose rise" style={{ animationDelay: '60ms' }} onSubmit={(e) => { e.preventDefault(); submit() }}>
          <div className="compose-head">
            <div className="segmented" role="tablist" aria-label="Input type">
              <button type="button" role="tab" aria-selected={mode === 'diff'} onClick={() => setMode('diff')}>
                <DiffIcon size={15} /> Paste diff
              </button>
              <button type="button" role="tab" aria-selected={mode === 'pr'} onClick={() => setMode('pr')}>
                <PrIcon size={15} /> GitHub PR
              </button>
            </div>
            <div className="samples">
              <span className="muted small">{sampleError ? 'Samples unavailable' : 'Try a sample'}</span>
              {samples.map((s) => (
                <button key={s.name} type="button" className="chip lift" aria-pressed={activeSample === s.name}
                  title={s.description} onClick={() => loadSample(s)} disabled={disabled}>
                  {s.title.split(' - ')[0]}
                </button>
              ))}
            </div>
          </div>

          {mode === 'diff' ? (
            <div className="editor">
              <label htmlFor="diff-input" className="sr-only">Unified diff</label>
              <textarea id="diff-input" spellCheck={false} value={diff} disabled={disabled} onKeyDown={onKeyDown}
                onChange={(e) => { setDiff(e.target.value); setActiveSample(null) }}
                placeholder={'Paste a unified diff, e.g. the output of\n\n  git diff main...my-branch'} />
            </div>
          ) : (
            <div className="pr-pane">
              <label htmlFor="pr-url" className="label">Pull request URL</label>
              <div className="input-wrap">
                <LinkIcon size={18} />
                <input id="pr-url" type="url" inputMode="url" autoComplete="off" value={url} disabled={disabled}
                  onKeyDown={onKeyDown} onChange={(e) => setUrl(e.target.value)}
                  placeholder="https://github.com/owner/repo/pull/123" />
              </div>
              <p className="muted small">Public repositories only, unless the server has a GitHub token.</p>
              <div className="feature-grid">
                <div className="feature"><strong>Reads full files</strong><span>At the PR’s head commit, not just the hunks.</span></div>
                <div className="feature"><strong>Finds existing tests</strong><span>Searches repository paths before flagging gaps.</span></div>
                <div className="feature"><strong>Treats PR text as untrusted</strong><span>Instructions hidden in the change are ignored.</span></div>
              </div>
            </div>
          )}

          {error && <p className="field-error" role="alert">{error}</p>}

          <div className="compose-foot">
            <span className="mono small muted">
              {mode === 'diff' && diff
                ? `${stats.files} file${stats.files === 1 ? '' : 's'} · +${stats.additions} −${stats.deletions} · ${diff.length.toLocaleString()} chars`
                : mode === 'pr' ? 'The agent fetches the diff and PR description' : 'Nothing to review yet'}
            </span>
            <div className="row-12">
              <span className="kbd-hint" aria-hidden="true"><kbd>{isMac ? '⌘' : 'Ctrl'}</kbd><kbd>↵</kbd></span>
              <button type="submit" className="btn btn-primary lift cta" disabled={!ready}>
                Review change <Arrow size={16} />
              </button>
            </div>
          </div>
        </form>
      </main>

      <aside className="layout-side stack-20">
        <section className="panel pad-20 rise" style={{ animationDelay: '120ms' }}>
          <h2 className="h2">What the agent checks</h2>
          <ul className="checks">
            {CHECKS.map((c) => (
              <li key={c.id}>
                <span className={`check-icon cat-${c.id}`}><CategoryIcon category={c.id} /></span>
                <div><strong>{c.title}</strong><p>{c.text}</p></div>
              </li>
            ))}
          </ul>
        </section>

        <section className="panel recent rise" style={{ animationDelay: '180ms' }}>
          <h2 className="h2">Recent reviews</h2>
          {history.length === 0 ? (
            <p className="muted small empty-recent">Reviews you run appear here.</p>
          ) : (
            <ul>
              {history.slice(0, 8).map((r) => (
                <li key={r.id}>
                  <button type="button" className="recent-row" onClick={() => onOpen(r.id)} disabled={disabled}>
                    <span className={`dot v-${r.status === 'failed' ? 'failed' : r.verdict ?? 'comment'}`} />
                    <span className="recent-text">
                      <span className="mono recent-label">{r.sourceLabel}</span>
                      <span className="muted small">
                        {r.status === 'failed' ? 'failed' : `${r.findingCount} finding${r.findingCount === 1 ? '' : 's'} · ${(r.verdict ?? '').replace('_', ' ')}`} · {timeAgo(r.createdAt)}
                      </span>
                    </span>
                    <ChevronRight size={15} />
                  </button>
                </li>
              ))}
            </ul>
          )}
        </section>
      </aside>
    </div>
  )
}
