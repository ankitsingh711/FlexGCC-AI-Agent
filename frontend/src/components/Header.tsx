import type { Health } from '../api'
import { Logo, Plus } from './Icons'

interface Props {
  health: Health | null | undefined
  running: boolean
  onHome?: () => void
  actions?: React.ReactNode
}

export function Header({ health, running, onHome, actions }: Props) {
  return (
    <header className="topbar">
      <button type="button" className="brand" onClick={onHome} aria-label="Code Review Agent — start a new review">
        <span className="brand-mark"><Logo /></span>
        <span>
          <span className="brand-name">Code Review Agent</span>
          <span className="brand-sub">AI review for diffs and pull requests</span>
        </span>
      </button>
      <nav className="topbar-actions" aria-label="Primary">
        <StatusPill health={health} running={running} />
        {actions}
      </nav>
    </header>
  )
}

function StatusPill({ health, running }: { health: Health | null | undefined; running: boolean }) {
  if (health === undefined) return <span className="pill"><span className="dot" />Connecting…</span>
  if (health === null) return <span className="pill pill-bad" role="status"><span className="dot" />API offline</span>
  if (!health.modelConfigured)
    return <span className="pill pill-warn" role="status" title={`Set ${health.apiKeyEnvVar} and restart the API`}><span className="dot" />API key not set</span>
  if (running) return <span className="pill pill-warn" role="status"><span className="dot pulse-amber" />Reviewing</span>
  return (
    <span className="pill" role="status">
      <span className="dot dot-ok pulse-ok" />
      <span className="muted">{health.provider}</span>
      <span className="mono small">{health.model}</span>
    </span>
  )
}

export function NewReviewButton({ onClick }: { onClick: () => void }) {
  return (
    <button type="button" className="btn btn-primary btn-sm lift" onClick={onClick}>
      <Plus size={15} /> New review
    </button>
  )
}
