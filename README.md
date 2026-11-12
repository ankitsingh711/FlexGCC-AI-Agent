# Code Review Agent

An AI agent that reviews a code change (a pasted `git diff` or a public GitHub PR URL) and returns structured feedback in four categories: **bugs, risks, missing tests, style**. Each finding has a severity, a confidence level, a `file:line` location, an explanation and a suggested fix. There is also an overall verdict (`approve` / `comment` / `request_changes`).

**Stack:** C# / ASP.NET Core 10 Web API · React 19 + TypeScript (Vite) · EF Core + SQLite · xUnit. **Model:** any OpenAI-compatible endpoint. Google Gemini's free tier (`gemini-3.8-flash`) is the default; Groq, OpenRouter, a local Ollama, or Claude (`claude-opus-5-5`, via the official Anthropic C# SDK) are each one setting away.

## Run it

```bash
export GEMINI_API_KEY=...                  # free key from Google AI Studio
export GITHUB_TOKEN=...                    # optional: raises GitHub rate limits
./scripts/run.sh                           # builds the UI, serves UI + API on http://localhost:5080
```

To use a different model, set `Agent:Provider` (and optionally `Agent:Model`) in `appsettings.json`, or the matching environment variables (`Agent__Provider=groq`):

| `Agent:Provider` | API key variable | Default model |
|---|---|---|
| `gemini` (default) | `GEMINI_API_KEY` | `gemini-3.8-flash` |
| `groq` | `GROQ_API_KEY` | `openai/gpt-oss-120b` |
| `openrouter` | `OPENROUTER_API_KEY` | none: set `Agent:Model`, e.g. one of its `:free` models |
| `openai-compatible` | `LLM_API_KEY` | none: set `Agent:Model` and `Agent:BaseUrl` (e.g. Ollama at `http://localhost:11434/v1`) |
| `anthropic` | `ANTHROPIC_API_KEY` | `claude-opus-5-5` |

- **UI:** paste a diff (or load one of the four samples in `samples/`) or enter a PR URL. The agent's tool calls stream live, and past runs are kept in History.
- **CLI / CI:** `git diff main | dotnet run --project backend/src/CodeReviewAgent.Cli -- --diff - --fail-on high`. It exits with code 1 if any finding is at or above the threshold, and `--json` prints machine-readable output. It reads the same key variables, plus `REVIEW_PROVIDER` and `REVIEW_MODEL`.
- **Tests:** `cd backend && dotnet test` runs 80 tests offline. The one live GitHub test does nothing unless `RUN_GITHUB_IT=1` is set.
- **Deploy:** the root `Dockerfile` builds the UI and API into one image, and `render.yaml` deploys it on Render's free tier (New → Blueprint → this repo, then enter `GEMINI_API_KEY`).
- **Dev mode:** start the API with `dotnet run --project backend/src/CodeReviewAgent.Api`, then run `npm run dev` in `frontend/` (Vite proxies `/api`).

## How it's built

```
React UI ──POST /api/reviews/stream (SSE)──▶ ReviewsController ──▶ ReviewService ──▶ CodeReviewAgent (tool-use loop)
                                                  │ SQLite history                    │            │
                                                  ▼                                   ▼            ▼
                                             ReviewRuns table              IChatModel            IChangeSource
                                                                   (Gemini/Groq/OpenRouter/      (pasted diff | GitHub PR)
                                                                    Ollama | Claude)
```

- **Agent loop (`Core/Agent/CodeReviewAgent.cs`).** I wrote the tool-use loop myself instead of using an SDK tool runner, because the loop has to stop on a *terminal* tool whose input is the actual product. It has to validate that input and send it back to the model when it's wrong. It also has to add budget warnings and stream progress for every tool call. History is append-only: each assistant turn is echoed back exactly in the provider's own format, and every tool call gets exactly one result.
- **Model providers (`Core/Llm/`).** The loop talks to a small model-neutral `IChatModel` interface with two implementations. `OpenAiCompatibleChatModel` covers Gemini, Groq, OpenRouter and Ollama: it retries 429 and 5xx responses with backoff that honours `Retry-After` (free tiers rate-limit hard), and it passes Gemini's tool-call thought signatures back untouched. `AnthropicChatModel` uses the official SDK with adaptive thinking, strict tool schemas, prompt caching and server-side refusal fallbacks.
- **Tools (read-only, scoped to the change):** `list_changed_files` and `get_file_diff`. For GitHub PRs only, `read_file` (any file at the head SHA, paged) and `search_repository_files` (e.g. to check whether tests exist). The tool set depends on the source, so a pasted diff never gets repository tools. The agent ends the review by calling `submit_review`, a **strict** JSON-schema tool.
- **Model settings:** `Agent:Effort` (default `high`) becomes `reasoning_effort` on OpenAI-compatible providers and adaptive-thinking effort on Claude. Reasoning summaries, when a provider returns them, appear in the live trace.
- **Grounding:** diffs are shown to the model with new-file line numbers in a gutter. `FindingValidator` then checks every finding against the parsed diff. Findings on files that aren't in the change are sent back to the model as a tool error so it can fix them. Line numbers outside the diff, unless the agent actually read that file, become file-level findings with a warning. It also removes duplicates, sorts by severity, and changes an "approve" to "request_changes" if a high-severity bug was reported.
- **Security:** PR titles, descriptions, diffs and tool output are marked as untrusted, and attempts to close the delimiter tags are neutralised. One sample (`prompt-injection-deploy.diff`) tries to tell the reviewer to approve. The PR URL parser only accepts `https://github.com/{owner}/{repo}/pull/{n}`, which prevents SSRF and path injection. `read_file` rejects traversal and URL-like paths. Reviews are rate-limited per IP, and every run is capped by turns, tool calls, diff size and wall-clock time.

## What went wrong and how I handled it

- **Strict tool schemas aren't portable.** Claude enforces the `submit_review` schema exactly. Other providers don't promise that, and some reject `additionalProperties` or `anyOf`. For those providers, schemas are reduced to the portable subset (nullable fields become optional). A malformed result is still caught by the validator and sent back to the model, and tool arguments that aren't valid JSON go back as an `INVALID_JSON` error instead of being run.
- **Free tiers rate-limit and fail in their own ways.** Groq's developer plan allows 8K tokens per minute, which is less than a single turn of this agent once a diff and tool results are in the conversation, so Gemini is the default. Gemini reports a bad key as HTTP 400 with an array-wrapped body, not 401; I found this by probing the real endpoint, and it is now mapped to a clear "API key rejected" error with a test that uses the real response shape.

- **Forced tool use isn't available.** Claude Opus 5.5 rejects `tool_choice: any/tool`, and smaller free models often answer in prose instead of calling a tool. So the loop never relies on forcing a call: the prompt names `submit_review`, and the loop nudges the model if a turn ends without one (it gives up after 2 nudges).
- **Bad findings shouldn't reach the user, and validation shouldn't loop forever.** A rejected submission goes back to the model with the list of valid paths. After `MaxSubmitRetries` attempts the validator repairs the submission itself (drops bad findings and records a warning) instead of using more turns.
- **Truncated output.** If a turn stops at `max_tokens`, a `tool_use` in it may have partial input that still parses. Those calls are never executed; the model gets an error result and is asked to resend.
- **A diff-parser edge case.** A removed line such as `--- a comment` (an SQL comment) looks like a file header. The parser reads hunk lines by the counts declared in the `@@` header, and there is a regression test for this case.
- **An SDK detail caught by a test.** `response.Model.ToString()` returned a JSON-quoted string (`"\"claude-opus-5-5\""`). A test caught it, and the fix uses `.Raw()`.
- **Toolchain drift.** EF Core 11 needs .NET 11, so I pinned the 10.0.x packages to stay on the .NET 10 LTS release.
- **Testing without paying for API calls.** The loop depends on an `IMessagesClient` seam. The tests replay scripted `BetaMessage` responses to cover tool use, rejected and repaired submissions, nudges, refusals, truncation, and turn and tool budgets. They also check the exact request sent (strict tools, `cache_control`, effort, fallbacks).

## What I'd do next

1. **An eval set:** label the planted bugs in `samples/` plus real PRs, then measure precision and recall per category so prompt or model changes are judged by data rather than impressions.
2. **GitHub integration:** a GitHub Action or App that posts findings as inline PR review comments, using `--fail-on` as a merge gate, with results cached per head SHA.
3. **Large PRs:** review files in parallel with a cheaper model and have a final pass merge and deduplicate the findings.
4. **Richer context tools:** symbol search and find-references, plus running the project's linters and tests in a sandbox so findings are backed by evidence.
5. **Production hardening:** authentication on the UI, EF migrations on SQL Server, OpenTelemetry traces for each tool call, and per-user cost tracking.
