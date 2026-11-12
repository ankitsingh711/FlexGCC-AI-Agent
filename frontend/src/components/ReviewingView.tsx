import { useEffect, useMemo, useRef, useState } from 'react'
import type { AgentEvent, ReviewRequest } from '../api'
import { parseDiff } from '../lib/diff'
import { fmtClock } from '../lib/format'
import { Bulb, Check, Wrench } from './Icons'

interface Props {
  request: ReviewRequest
  events: AgentEvent[]
  maxTurns: number
  onCancel: () => void
}

/** The live screen: meters, the agent's timeline as it streams, and a scan over the diff being read. */
export function ReviewingView({ request, events, maxTurns, onCancel }: Props) {
  const [elapsed, setElapsed] = useState(0)
  useEffect(() => {
    const startedAt = Date.now()
    const t = setInterval(() => setElapsed(Math.floor((Date.now() - startedAt) / 1000)), 500)
    return () => clearInterval(t)
  }, [])

  const turn = events.reduce((n, e) => Math.max(n, e.turn), 0)
  const toolCalls = events.filter((e) => e.type === 'tool_call').length
  const label = request.pullRequestUrl ? request.pullRequestUrl.replace(/^https:\/\/github\.com\//, '') : 'Pasted diff'

  // The file the agent touched most recently, or the first changed file.
  const files = useMemo(() => (request.diff ? parseDiff(request.diff) : []), [request.diff])
  const lastPath = [...events].reverse().find((e) => e.type === 'tool_call' && e.message.startsWith('path='))?.message.slice(5)
  const scanFile = files.find((f) => f.path === lastPath) ?? files[0]

  const end = useRef<HTMLLIElement>(null)
  // Block body on purpose: in current Chromium scrollIntoView() returns a Promise, and an expression-bodied effect
  // would hand that Promise to React as its "cleanup" — React then calls it and the whole app unmounts.
  useEffect(() => {
    end.current?.scrollIntoView({ block: 'nearest', behavior: 'smooth' })
  }, [events.length])

  const steps = events.filter((e) => e.type !== 'tool_result')
  const results = new Map<number, AgentEvent>()
  events.forEach((e, i) => {
    if (e.type === 'tool_result') {
      // Attach each result to the most recent unmatched call of the same tool.
      for (let j = i - 1; j >= 0; j--) {
        if (events[j].type === 'tool_call' && events[j].tool === e.tool && !results.has(j)) {
          results.set(j, e)
          break
        }
      }
    }
  })

  return (
    <div className="stack-24">
      <section className="panel run-banner rise" aria-live="polite">
        <div className="progress-track"><div className="progress-bar" /></div>
        <div className="run-banner-body">
          <div className="min-0">
            <div className="eyebrow">Reviewing</div>
            <h1 className="h1 mono-title">{label}</h1>
          </div>
          <div className="meters">
            <Meter label="Turn" value={`${Math.max(turn, 1)} / ${maxTurns}`} />
            <Meter label="Tool calls" value={String(toolCalls)} />
            <Meter label="Elapsed" value={fmtClock(elapsed)} />
            <button type="button" className="btn btn-ghost lift" onClick={onCancel}>Cancel</button>
          </div>
        </div>
      </section>

      <div className="layout">
        <section className="panel pad-24 layout-main" aria-label="Agent trace">
          <div className="section-head">
            <h2 className="h2">Agent trace</h2>
            <span className="muted small">Live</span>
          </div>
          <ol className="timeline" aria-live="polite">
            {steps.length === 0 && <TimelineItem kind="active" title="Starting review" meta="" last />}
            {steps.map((e, i) => {
              const idx = events.indexOf(e)
              const last = i === steps.length - 1
              if (e.type === 'tool_call') {
                const r = results.get(idx)
                const meta = [e.message !== '{}' ? e.message : '', r ? `${r.isError ? 'error' : 'ok'} · ${r.durationMs} ms` : 'running…'].filter(Boolean).join(' · ')
                return <TimelineItem key={idx} kind={r ? (r.isError ? 'error' : 'tool') : 'active'} title={e.tool ?? 'tool'} meta={meta} mono last={last}
                  detail={r?.isError ? r.message : undefined} />
              }
              if (e.type === 'thinking') return <TimelineItem key={idx} kind="think" title="Reasoning" meta="" detail={e.message} last={last} />
              if (e.type === 'message') return <TimelineItem key={idx} kind="think" title="Note" meta="" detail={e.message} last={last} />
              if (e.type === 'turn') return <TimelineItem key={idx} kind={last ? 'active' : 'done'} title={`Turn ${e.turn}`} meta={last ? 'thinking…' : ''} last={last} />
              return <TimelineItem key={idx} kind="done" title={e.message} meta="" last={last} />
            })}
            <li ref={end} aria-hidden="true" />
          </ol>
        </section>

        <aside className="layout-side stack-24">
          <section className="panel scan-panel" aria-label="Diff being read">
            <div className="scan-head">
              <span className="mono small">{scanFile?.path ?? (request.pullRequestUrl ? 'Fetching pull request…' : 'diff')}</span>
              <span className="muted small">reading</span>
            </div>
            <div className="scan-body">
              {scanFile ? (
                scanFile.lines.filter((l) => l.kind !== 'hunk').slice(0, 18).map((l, i) => (
                  <div key={i} className={`code-row k-${l.kind}`}>
                    <span className="ln">{l.newNo ?? ''}</span>
                    <span className="code-text">{l.text || ' '}</span>
                  </div>
                ))
              ) : (
                Array.from({ length: 12 }, (_, i) => <span key={i} className="sk sk-line" style={{ width: `${50 + ((i * 37) % 45)}%` }} />)
              )}
              <div className="scanline" aria-hidden="true" />
            </div>
          </section>

          <section className="panel pad-20">
            <h2 className="h2">Findings</h2>
            <p className="muted small">They appear once the agent submits its review.</p>
            <div className="stack-12 mt-12">
              {[['92%', '64%'], ['84%', '71%'], ['88%', '52%']].map(([a, b], i) => (
                <div key={i} className="sk-card">
                  <div className="row-8"><span className="sk" style={{ width: 64, height: 20, borderRadius: 999 }} /><span className="sk" style={{ width: 140, height: 20 }} /></div>
                  <span className="sk" style={{ width: a, height: 14 }} />
                  <span className="sk" style={{ width: b, height: 14 }} />
                </div>
              ))}
            </div>
          </section>
        </aside>
      </div>
    </div>
  )
}

function Meter({ label, value }: { label: string; value: string }) {
  return (
    <div className="meter">
      <div className="meter-label">{label}</div>
      <div className="meter-value">{value}</div>
    </div>
  )
}

type Kind = 'done' | 'tool' | 'think' | 'active' | 'error'

function TimelineItem({ kind, title, meta, detail, mono, last }: { kind: Kind; title: string; meta: string; detail?: string; mono?: boolean; last: boolean }) {
  return (
    <li className="tl-item">
      <div className="tl-rail">
        <span className={`tl-node n-${kind}`}>
          {kind === 'done' && <Check size={13} />}
          {kind === 'tool' && <Wrench size={13} />}
          {kind === 'error' && <Wrench size={13} />}
          {kind === 'think' && <Bulb size={13} />}
          {kind === 'active' && <span className="spinner" />}
        </span>
        {!last && <span className="tl-line" />}
      </div>
      <div className="tl-body">
        <div className="tl-title-row">
          <span className={mono ? 'tl-title mono accent-text' : kind === 'think' ? 'tl-title muted' : 'tl-title'}>{title}</span>
          {meta && <span className="mono small muted">{meta}</span>}
        </div>
        {detail && (
          kind === 'think'
            ? <details className="tl-detail"><summary>Show reasoning</summary><p>{detail}</p></details>
            : <pre className="tl-detail err">{detail}</pre>
        )}
        {kind === 'active' && (
          <div className="typing" aria-hidden="true"><span /><span /><span /></div>
        )}
      </div>
    </li>
  )
}
