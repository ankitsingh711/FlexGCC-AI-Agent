// Minimal unified-diff reader for display: per-file lines with new-file line numbers.
// The server's parser is the source of truth for validation; this only renders.

export type LineKind = 'add' | 'del' | 'ctx' | 'hunk'

export interface DiffLine {
  kind: LineKind
  newNo?: number
  text: string
}

export interface DiffFile {
  path: string
  additions: number
  deletions: number
  lines: DiffLine[]
}

const HUNK = /^@@ -\d+(?:,(\d+))? \+(\d+)(?:,(\d+))? @@ ?(.*)$/

export function parseDiff(text: string): DiffFile[] {
  const files: DiffFile[] = []
  let file: DiffFile | null = null
  let newNo = 0
  // Remaining old/new lines in the current hunk; the hunk ends when both reach zero (as in the server parser).
  let oldLeft = 0
  let newLeft = 0
  const inHunkNow = () => oldLeft > 0 || newLeft > 0

  for (const raw of text.replace(/\r\n/g, '\n').split('\n')) {
    if (raw.startsWith('diff --git ')) {
      const m = / b\/(.+)$/.exec(raw)
      file = { path: m?.[1] ?? raw.slice(11), additions: 0, deletions: 0, lines: [] }
      files.push(file)
      oldLeft = newLeft = 0
      continue
    }
    const inHunk = inHunkNow()
    if (!inHunk && raw.startsWith('+++ ')) {
      const p = raw.slice(4).split('\t')[0].trim()
      if (p !== '/dev/null') {
        const path = p.replace(/^[ab]\//, '')
        if (!file || file.lines.length > 0) {
          file = { path, additions: 0, deletions: 0, lines: [] }
          files.push(file)
        } else file.path = path
      }
      continue
    }
    if (!inHunk && raw.startsWith('--- ')) {
      if (!file || file.lines.length > 0) {
        file = { path: raw.slice(4).replace(/^[ab]\//, ''), additions: 0, deletions: 0, lines: [] }
        files.push(file)
      }
      continue
    }
    const h = HUNK.exec(raw)
    if (h && file) {
      oldLeft = h[1] === undefined ? 1 : Number(h[1])
      newNo = Number(h[2])
      newLeft = h[3] === undefined ? 1 : Number(h[3])
      file.lines.push({ kind: 'hunk', text: raw })
      continue
    }
    if (!file || raw.startsWith('\\')) continue // "\ No newline at end of file"
    if (!inHunk) continue
    if (raw.startsWith('+')) {
      file.lines.push({ kind: 'add', newNo: newNo++, text: raw.slice(1) })
      file.additions++
      newLeft--
    } else if (raw.startsWith('-')) {
      file.lines.push({ kind: 'del', text: raw.slice(1) })
      file.deletions++
      oldLeft--
    } else if (raw.startsWith(' ') || raw === '') {
      file.lines.push({ kind: 'ctx', newNo: newNo++, text: raw.slice(1) })
      oldLeft--
      newLeft--
    } else {
      oldLeft = newLeft = 0
    }
  }
  return files
}

export function diffStats(files: DiffFile[]) {
  return {
    files: files.length,
    additions: files.reduce((n, f) => n + f.additions, 0),
    deletions: files.reduce((n, f) => n + f.deletions, 0),
  }
}

export interface SnippetLine {
  no?: number
  text: string
  hit: boolean
  kind: LineKind
}

/** Lines around a cited new-file line, taken only from what the diff shows. */
export function snippet(files: DiffFile[], path: string, line?: number, endLine?: number, context = 2): SnippetLine[] {
  if (!line) return []
  const file = files.find((f) => f.path === path)
  if (!file) return []
  const end = endLine && endLine > line ? endLine : line
  const idx = file.lines.findIndex((l) => l.newNo === line)
  if (idx < 0) return []
  const out: SnippetLine[] = []
  for (let i = Math.max(0, idx - context); i < file.lines.length; i++) {
    const l = file.lines[i]
    if (l.kind === 'hunk') {
      if (i > idx) break
      continue
    }
    if (l.newNo !== undefined && l.newNo > end + context) break
    out.push({ no: l.newNo, text: l.text, hit: l.newNo !== undefined && l.newNo >= line && l.newNo <= end, kind: l.kind })
  }
  return out
}
