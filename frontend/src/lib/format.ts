import type { Category, Finding, ReviewRun, Severity, Verdict } from '../api'

export const SEVERITIES: Severity[] = ['critical', 'high', 'medium', 'low', 'info']

export const CATEGORY_LABEL: Record<Category, string> = {
  bug: 'Bugs',
  risk: 'Risks',
  missing_test: 'Missing tests',
  style: 'Style',
}

export const VERDICT_LABEL: Record<Verdict, string> = {
  approve: 'Approve',
  comment: 'Comment',
  request_changes: 'Request changes',
}

export const fmtTokens = (n: number) => (n >= 1000 ? `${(n / 1000).toFixed(1)}k` : String(n))

export const fmtDuration = (ms: number) => `${(ms / 1000).toFixed(1)} s`

export const fmtClock = (seconds: number) =>
  `${String(Math.floor(seconds / 60)).padStart(2, '0')}:${String(seconds % 60).padStart(2, '0')}`

export function timeAgo(iso: string): string {
  const s = Math.max(0, (Date.now() - new Date(iso).getTime()) / 1000)
  if (s < 60) return 'just now'
  if (s < 3600) return `${Math.floor(s / 60)}m ago`
  if (s < 86400) return `${Math.floor(s / 3600)}h ago`
  return new Date(iso).toLocaleDateString()
}

export const location = (f: Finding) =>
  f.line ? `${f.file}:${f.line}${f.endLine ? `-${f.endLine}` : ''}` : f.file

/** A Markdown review, ready to paste into a PR comment. */
export function toMarkdown(run: ReviewRun): string {
  const r = run.result
  if (!r) return ''
  const lines = [
    `## Code review: ${VERDICT_LABEL[r.verdict]}`,
    '',
    `_${run.sourceLabel} · ${r.stats.filesChanged} file(s), +${r.stats.additions} −${r.stats.deletions} · ${r.findings.length} finding(s)_`,
    '',
    r.summary,
  ]
  for (const cat of Object.keys(CATEGORY_LABEL) as Category[]) {
    const items = r.findings.filter((f) => f.category === cat)
    if (items.length === 0) continue
    lines.push('', `### ${CATEGORY_LABEL[cat]}`)
    for (const f of items) {
      lines.push('', `- **[${f.severity}] ${f.title}** — \`${location(f)}\``, `  ${f.explanation}`)
      if (f.suggestion) lines.push('', '  ```', ...f.suggestion.split('\n').map((l) => `  ${l}`), '  ```')
    }
  }
  return lines.join('\n')
}

export function download(filename: string, content: string, type: string) {
  const url = URL.createObjectURL(new Blob([content], { type }))
  const a = document.createElement('a')
  a.href = url
  a.download = filename
  a.click()
  URL.revokeObjectURL(url)
}
