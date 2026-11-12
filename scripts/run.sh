#!/usr/bin/env bash
# Build the React UI into the API's wwwroot and start the API on http://localhost:5080 (UI + API, one process).
set -euo pipefail
cd "$(dirname "$0")/.."

# The model provider comes from Agent:Provider (default: gemini). Each provider reads its own key.
if [[ -z "${GEMINI_API_KEY:-}${GROQ_API_KEY:-}${OPENROUTER_API_KEY:-}${LLM_API_KEY:-}${ANTHROPIC_API_KEY:-}" ]]; then
  echo "warning: no model API key is set (e.g. GEMINI_API_KEY); the UI will load but reviews will return 503." >&2
fi

(cd frontend && npm install --silent && npm run build)
cd backend/src/CodeReviewAgent.Api
exec dotnet run --launch-profile http
