import { useCallback, useEffect, useRef, useState } from 'react'
import { api, ApiError, streamReview, type AgentEvent, type Health, type ReviewRequest, type ReviewRun, type ReviewSummary } from './api'
import { ComposeView } from './components/ComposeView'
import { Header, NewReviewButton } from './components/Header'
import { Retry, Warning } from './components/Icons'
import { ResultsView } from './components/ResultsView'
import { ReviewingView } from './components/ReviewingView'

/**
 * Three screens, one state machine: compose → reviewing → results (or an error, with retry).
 * Matches the "Compose / Live review / Results" artboards of the design.
 */
type View =
  | { name: 'compose' }
  | { name: 'reviewing'; request: ReviewRequest }
  | { name: 'results'; run: ReviewRun }
  | { name: 'error'; error: ApiError; request?: ReviewRequest }

export default function App() {
  // undefined = still loading, null = unreachable.
  const [health, setHealth] = useState<Health | null | undefined>(undefined)
  const [view, setView] = useState<View>({ name: 'compose' })
  const [events, setEvents] = useState<AgentEvent[]>([])
  const [history, setHistory] = useState<ReviewSummary[]>([])
  const [lastRequest, setLastRequest] = useState<ReviewRequest | null>(null)
  const abort = useRef<AbortController | null>(null)

  const refreshHistory = useCallback(() => {
    api.history().then(setHistory).catch(() => {})
  }, [])

  useEffect(() => {
    api.health().then(setHealth).catch(() => setHealth(null))
    refreshHistory()
  }, [refreshHistory])

  // Results are addressable (#/reviews/<id>): bookmarkable, shareable, and the Back button returns to compose.
  // The URL is the external system here; state only changes after the fetch resolves or on hashchange.
  useEffect(() => {
    const sync = async () => {
      const id = reviewIdFromHash()
      if (!id) {
        setView((v) => (v.name === 'reviewing' ? v : { name: 'compose' }))
        return
      }
      try {
        const r = await api.review(id)
        setView(r.result ? { name: 'results', run: r } : { name: 'error', error: new ApiError('This review failed', r.error ?? 'No result was recorded.') })
      } catch (e) {
        setView({ name: 'error', error: e instanceof ApiError ? e : new ApiError('Review not found', (e as Error).message) })
      }
    }
    const initial = reviewIdFromHash()
    if (initial) {
      api.review(initial)
        .then((r) => setView(r.result ? { name: 'results', run: r } : { name: 'error', error: new ApiError('This review failed', r.error ?? 'No result was recorded.') }))
        .catch((e) => setView({ name: 'error', error: e instanceof ApiError ? e : new ApiError('Review not found', (e as Error).message) }))
    }
    window.addEventListener('hashchange', sync)
    return () => window.removeEventListener('hashchange', sync)
  }, [])

  // Keep the browser tab informative while a review runs in the background.
  useEffect(() => {
    document.title =
      view.name === 'reviewing' ? 'Reviewing… · Code Review Agent'
        : view.name === 'results' && view.run.result ? `${view.run.result.findings.length} findings · Code Review Agent`
          : 'Code Review Agent'
  }, [view])

  const run = async (request: ReviewRequest) => {
    abort.current = new AbortController()
    setLastRequest(request)
    setEvents([])
    setView({ name: 'reviewing', request })
    window.scrollTo({ top: 0, behavior: 'smooth' })
    try {
      const result = await streamReview(request, (e) => setEvents((prev) => [...prev, e]), abort.current.signal)
      if (result.status === 'failed' || !result.result) {
        setView({ name: 'error', error: new ApiError('Review failed', result.error ?? 'No result was produced.'), request })
      } else {
        setView({ name: 'results', run: result })
        window.history.pushState(null, '', `#/reviews/${result.id}`)
      }
    } catch (e) {
      if ((e as Error).name === 'AbortError') setView({ name: 'compose' })
      else setView({ name: 'error', error: e instanceof ApiError ? e : new ApiError('Request failed', (e as Error).message), request })
    } finally {
      refreshHistory()
    }
  }

  const open = (id: string) => {
    window.location.hash = `#/reviews/${id}` // hashchange → loadFromHash
    window.scrollTo({ top: 0, behavior: 'smooth' })
  }

  const home = () => {
    if (view.name === 'reviewing') return
    if (window.location.hash) window.history.pushState(null, '', window.location.pathname)
    setView({ name: 'compose' })
  }

  const running = view.name === 'reviewing'

  return (
    <div className="app">
      <Header health={health} running={running} onHome={home}
        actions={view.name === 'results' || view.name === 'error' ? <NewReviewButton onClick={home} /> : null} />

      {health && !health.modelConfigured && view.name === 'compose' && (
        <div className="banner rise" role="status">
          <Warning size={16} />
          <span>
            The <strong>{health.provider}</strong> provider needs <code>{health.apiKeyEnvVar}</code>. Set it and restart the
            server to run reviews.
          </span>
        </div>
      )}

      <div className="view" key={view.name}>
        {view.name === 'compose' && (
          <ComposeView disabled={running} history={history} initial={lastRequest} onSubmit={run} onOpen={open} />
        )}
        {view.name === 'reviewing' && (
          <ReviewingView request={view.request} events={events} maxTurns={health?.maxTurns ?? 12} onCancel={() => abort.current?.abort()} />
        )}
        {view.name === 'results' && <ResultsView run={view.run} />}
        {view.name === 'error' && (
          <section className="panel error-panel rise" role="alert">
            <span className="error-icon"><Warning size={24} /></span>
            <h1 className="h1">{view.error.title}</h1>
            {view.error.message !== view.error.title && <p className="muted">{view.error.message}</p>}
            <div className="row-12 center">
              <button type="button" className="btn btn-ghost lift" onClick={home}>Edit input</button>
              {view.request && (
                <button type="button" className="btn btn-primary lift" onClick={() => run(view.request!)}><Retry size={15} /> Try again</button>
              )}
            </div>
          </section>
        )}
      </div>
    </div>
  )
}

function reviewIdFromHash(): string | undefined {
  return /^#\/reviews\/([0-9a-f-]{36})$/i.exec(window.location.hash)?.[1]
}
