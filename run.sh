#!/usr/bin/env bash
# One-command entry points for the Agentic SDLC prototype (macOS / Linux / Git Bash).
#   ./run.sh test                         build everything and run both test suites
#   ./run.sh scenario greenfield          run a scenario headless (interactive approvals)
#   ./run.sh scenario brownfield --auto   same, auto-approve (CI/demo)
#   ./run.sh serve                        API + dashboard on http://localhost:5080
#   ./run.sh shortener                    run the URL shortener itself on http://localhost:5000
set -euo pipefail
cd "$(dirname "$0")"
HOST=src/AgenticSdlc.Host
cmd="${1:-serve}"; shift || true

case "$cmd" in
  test)      dotnet test -nologo ;;
  scenario)
    scenario="${1:-greenfield}"; shift || true
    args=(run "$scenario")
    for a in "$@"; do
      case "$a" in
        --auto) args+=(--auto-approve) ;;
        *) args+=("$a") ;;
      esac
    done
    dotnet run --project "$HOST" -- "${args[@]}" ;;
  serve)
    if [ ! -f dashboard/dist/dashboard/browser/index.html ]; then
      echo "Dashboard not built yet; building it (requires Node 24)..."
      (cd dashboard && npm install && npm run build)
    fi
    dotnet run --project "$HOST" -- serve --port 5080 ;;
  shortener) dotnet run --project src/UrlShortener.Api --urls http://localhost:5000 ;;
  dashboard-build) (cd dashboard && npm install && npm run build) ;;
  list)      dotnet run --project "$HOST" -- list ;;
  metrics)   dotnet run --project "$HOST" -- metrics ;;
  *) echo "unknown command: $cmd"; exit 2 ;;
esac
