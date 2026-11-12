import { useMemo, useState } from 'react'
import type { Category, Finding, ReviewRun } from '../api'
import { parseDiff, snippet, type DiffFile } from '../lib/diff'
import { CATEGORY_LABEL, SEVERITIES, VERDICT_LABEL, download, fmtDuration, fmtTokens, location, toMarkdown } from '../lib/format'
import { Check, Chevron, Copy, Download, Info, Shield, Warning } from './Icons'

type Filter = Category | 'all'
const FILTERS: Filter[] = ['all', 'bug', 'risk', 'missing_test', 'style']

export function ResultsView({ run }: { run: ReviewRun }) {
  const result = run.result!
  const files = useMemo(() => parseDiff(run.diff ?? ''), [run.diff])
  const [filter, setFilter] = useState<Filter>('all')
  // Open the first two findings by default: the most severe ones are what people came for.
  const [open, setOpen] = useState<Set<number>>(() => new Set([0, 1]))
  const [copied, setCopied] = useState(false)

  const counts = useMemo(() => {
    const c: Record<string, number> = { all: result.findings.length }
    for (const f of result.findings) c[f.category] = (c[f.category] ?? 0) + 1
    return c
  }, [result.findings])
  const bySeverity = SEVERITIES.map((s) => ({ s, n: result.findings.filter((f) => f.severity === s).length })).filter((x) => x.n > 0)
  const visible = result.findings.map((f, i) => ({ f, i })).filter(({ f }) => filter === 'all' || f.category === filter)

  const u = result.usage
  const totalIn = u.inputTokens + u.cacheReadInputTokens + u.cacheCreationInputTokens
  const cacheHit = totalIn ? Math.round((100 * u.cacheReadInputTokens) / totalIn) : 0

  const copyMarkdown = async () => {
    await navigator.clipboard.writeText(toMarkdown(run))
    setCopied(true)
    setTimeout(() => setCopied(false), 1800)
  }

  const toggle = (i: number) =>
    setOpen((prev) => {
      const next = new Set(prev)
      if (next.has(i)) next.delete(i)
      else next.add(i)
      return next
    })

  return (
    <div className="stack-28">
      <section className={`panel verdict-hero v-${result.verdict} rise`} aria-label="Verdict">
        <div className="verdict-top">
          <div className="verdict-main">
            <span className="verdict-icon pop" aria-hidden="true">
              {result.verdict === 'approve' ? <Shield size={26} /> : result.verdict === 'comment' ? <Info size={26} /> : <Warning size={26} />}
            </span>
            <div className="min-0">
              <div className="row-10 wrap">
                <span className="eyebrow muted-eyebrow">Verdict</span>
                <span className="tag mono">{run.sourceLabel}</span>
              </div>
              <h1 className="verdict-title">{VERDICT_LABEL[result.verdict]}</h1>
              <p className="verdict-summary">{result.summary}</p>
            </div>
          </div>
          <div className="severity">
            <div className="severity-head"><span>Severity</span><span className="mono">{result.findings.length} finding{result.findings.length === 1 ? '' : 's'}</span></div>
            {bySeverity.length === 0 ? (
              <div className="severity-empty">No findings — nothing worth flagging.</div>
            ) : (
              <>
                <div className="severity-bar grow" role="img" aria-label={bySeverity.map((x) => `${x.n} ${x.s}`).join(', ')}>
                  {bySeverity.map((x) => <span key={x.s} className={`bg-${x.s}`} style={{ flex: x.n }} />)}
                </div>
                <div className="severity-legend">
                  {bySeverity.map((x) => (
                    <span key={x.s}><span className={`swatch bg-${x.s}`} />{x.s}<span className="mono muted">{x.n}</span></span>
                  ))}
                </div>
              </>
            )}
          </div>
        </div>
        <dl className="stat-grid">
          <Stat label="Files" value={`${result.stats.filesChanged}  +${result.stats.additions} −${result.stats.deletions}`} />
          <Stat label="Turns" value={String(result.turns)} />
          <Stat label="Tool calls" value={String(result.toolCalls.length)} />
          <Stat label="Tokens in / out" value={`${fmtTokens(totalIn)} / ${fmtTokens(u.outputTokens)}`} />
          <Stat label="Cache hit" value={`${cacheHit}%`} />
          <Stat label="Duration" value={fmtDuration(result.durationMs)} />
        </dl>
      </section>

      <div className="layout">
        <main className="layout-main">
          <div className="findings-toolbar">
            <div className="segmented wrap" role="tablist" aria-label="Filter findings">
              {FILTERS.map((f) => (
                <button key={f} type="button" role="tab" aria-selected={filter === f} onClick={() => setFilter(f)} disabled={f !== 'all' && !counts[f]}>
                  {f === 'all' ? 'All' : CATEGORY_LABEL[f]} <span className="count">{counts[f] ?? 0}</span>
                </button>
              ))}
            </div>
            <span className="muted small">Sorted by severity</span>
          </div>

          {visible.length === 0 ? (
            <div className="panel pad-24 empty-findings rise">
              <Shield size={28} />
              <h2 className="h2">{filter === 'all' ? 'Nothing to flag' : 'Nothing in this category'}</h2>
              <p className="muted">{filter === 'all' ? 'The agent reviewed every changed file and found no material issues.' : 'Try another filter.'}</p>
            </div>
          ) : (
            <ul className="findings">
              {visible.map(({ f, i }, k) => (
                <FindingCard key={i} finding={f} files={files} open={open.has(i)} onToggle={() => toggle(i)} delay={120 + k * 50} />
              ))}
            </ul>
          )}
        </main>

        <aside className="layout-side stack-20">
          <section className="panel pad-20 rise" style={{ animationDelay: '150ms' }}>
            <h2 className="h2">Export</h2>
            <div className="stack-8 mt-12">
              <button type="button" className="btn btn-ghost lift full" onClick={copyMarkdown}>
                {copied ? <Check size={15} /> : <Copy size={15} />} {copied ? 'Copied to clipboard' : 'Copy as Markdown'}
              </button>
              <button type="button" className="btn btn-ghost lift full"
                onClick={() => download(`review-${run.id.slice(0, 8)}.json`, JSON.stringify(run.result, null, 2), 'application/json')}>
                <Download size={15} /> Export JSON
              </button>
            </div>
          </section>

          <section className="panel pad-20 rise" style={{ animationDelay: '200ms' }}>
            <h2 className="h2">How the agent got here</h2>
            <ol className="trace-summary">
              {result.toolCalls.map((t, i) => (
                <li key={i} className={t.isError ? 'err' : ''}>
                  <span className="mono muted turn">T{t.turn}</span>
                  <span className="mono call">{t.name}{t.inputSummary && t.inputSummary !== '{}' && t.name !== 'submit_review' ? `  ${t.inputSummary}` : ''}</span>
                  <span className="mono muted small">{t.isError ? 'error' : `${t.durationMs} ms`}</span>
                </li>
              ))}
            </ol>
            <p className="muted small mt-12">{result.model}</p>
          </section>

          {result.warnings.length > 0 && (
            <section className="panel pad-20 rise" style={{ animationDelay: '250ms' }}>
              <h2 className="h2">Validation notes</h2>
              <p className="muted small">Every finding is checked against the diff before it is shown.</p>
              <ul className="notes">
                {result.warnings.map((w, i) => <li key={i}><Info size={16} /><span>{w}</span></li>)}
              </ul>
            </section>
          )}

          <section className="panel pad-20 rise" style={{ animationDelay: '300ms' }}>
            <h2 className="h2">Use in CI</h2>
            <pre className="code-block mt-12">{'git diff main | review-agent \\\n  --diff - --fail-on high'}</pre>
            <p className="muted small mt-8">Exits 1 when a finding is at or above the threshold.</p>
          </section>
        </aside>
      </div>
    </div>
  )
}

function FindingCard({ finding: f, files, open, onToggle, delay }: { finding: Finding; files: DiffFile[]; open: boolean; onToggle: () => void; delay: number }) {
  const [copied, setCopied] = useState(false)
  const code = useMemo(() => snippet(files, f.file, f.line, f.endLine), [files, f.file, f.line, f.endLine])
  const id = `finding-${f.file}-${f.line}-${f.title}`.replace(/[^\w-]/g, '_')

  const copyFix = async () => {
    await navigator.clipboard.writeText(f.suggestion ?? '')
    setCopied(true)
    setTimeout(() => setCopied(false), 1500)
  }

  return (
    <li className={`panel finding sev-${f.severity} rise`} style={{ animationDelay: `${delay}ms` }}>
      <button type="button" className="finding-toggle" aria-expanded={open} aria-controls={id} onClick={onToggle}>
        <span className="finding-meta">
          <span className={`sev-pill sev-${f.severity}`}>{f.severity}</span>
          <span className="cat-pill">{f.category.replace('_', ' ')}</span>
          <span className="tag mono">{location(f)}</span>
          <span className="muted small conf">{f.confidence} confidence</span>
          <span className={open ? 'chev open' : 'chev'}><Chevron /></span>
        </span>
        <span className="finding-title">{f.title}</span>
        <span className="finding-expl">{f.explanation}</span>
      </button>
      {open && (
        <div className="finding-body" id={id}>
          {code.length > 0 && (
            <div className="code-snippet" aria-label={`Code at ${location(f)}`}>
              {code.map((l, i) => (
                <div key={i} className={`code-row k-${l.kind} ${l.hit ? 'hit' : ''}`}>
                  <span className="ln">{l.no ?? ''}</span>
                  <span className="code-text">{l.text || ' '}</span>
                </div>
              ))}
            </div>
          )}
          {f.suggestion && (
            <div className="fix">
              <div className="fix-head">
                <span>Suggested fix</span>
                <button type="button" className="icon-btn" onClick={copyFix} aria-label={copied ? 'Copied' : 'Copy suggested fix'}>
                  {copied ? <Check size={15} /> : <Copy size={15} />}
                </button>
              </div>
              <pre>{f.suggestion}</pre>
            </div>
          )}
        </div>
      )}
    </li>
  )
}

function Stat({ label, value }: { label: string; value: string }) {
  return (
    <div className="stat">
      <dt>{label}</dt>
      <dd>{value}</dd>
    </div>
  )
}
