# Agentic SDLC Orchestrator + URL Shortener

A working prototype of an **agentic software-delivery system**: a governed orchestration engine that takes a
requirement and drives it through requirements → design → implementation ∥ tests → verification ∥ docs → release,
with an explicit dependency graph, entry/exit gates, human approval checkpoints, bounded retries, repair cycles,
rollback, safe-stop, policy guardrails, a tamper-evident audit log and reliability metrics.

The workload it builds and evolves is a **URL shortener** (ASP.NET Core minimal API, .NET 10, SQLite) with analytics
and reliability features. Three scenarios exercise the engine: **greenfield**, **brownfield** and **ambiguous**.

| | |
|---|---|
| Engine, agents, policy, audit, metrics | `src/AgenticSdlc.Core` · 33 tests |
| CLI + HTTP API host | `src/AgenticSdlc.Host` |
| Angular dashboard (approvals, stage graph, audit, metrics) | `dashboard/` |
| URL shortener (the product) | `src/UrlShortener.Api` · 49 tests |
| Architecture | [ARCHITECTURE.md](ARCHITECTURE.md) |
| Scenarios | [greenfield](docs/scenarios/01-greenfield.md) · [brownfield](docs/scenarios/02-brownfield.md) · [ambiguous](docs/scenarios/03-ambiguous.md) |
| Testing approach, limitations, trade-offs | [docs/TESTING.md](docs/TESTING.md) · [docs/ENGINEERING_SUMMARY.md](docs/ENGINEERING_SUMMARY.md) |

## Quick start (clone and run, no API key needed)

Prerequisites: [.NET 10 SDK](https://dotnet.microsoft.com/download). Node 24 only if you want the dashboard.

```bash
git clone <this repo> && cd agentic-sdlc

# 1. Everything builds and both test suites pass (~15 s)
dotnet test

# 2. Run a scenario headless. The verification stage builds and tests the GENERATED code with the real toolchain.
dotnet run --project src/AgenticSdlc.Host -- run greenfield --auto-approve

# 3. Run one with real human checkpoints: you will be asked to clarify the requirement and approve changes.
dotnet run --project src/AgenticSdlc.Host -- run ambiguous

# 4. Watch governance kick in: inject a failure → bounded repair → rollback to the baseline (exit code 1)
dotnet run --project src/AgenticSdlc.Host -- run brownfield --auto-approve --inject-failure verification

# 5. Inspect
dotnet run --project src/AgenticSdlc.Host -- list
dotnet run --project src/AgenticSdlc.Host -- audit <runId> --verify
dotnet run --project src/AgenticSdlc.Host -- metrics
```

`run.ps1` / `run.sh` wrap the same commands (`.\run.ps1 test`, `.\run.ps1 scenario brownfield -Auto`, `.\run.ps1 serve`).

### Dashboard (optional)

```bash
cd dashboard && npm install && npm run build && cd ..
dotnet run --project src/AgenticSdlc.Host -- serve        # http://localhost:5080
```

Start runs, answer clarifications, approve or reject change sets, follow the stage graph by wave, read the decision
lineage, artifacts, the live audit tail and the generated workspace. For development: `npm start` in `dashboard/`
proxies `/api` to the host.

### Run the URL shortener itself

```bash
dotnet run --project src/UrlShortener.Api --urls http://localhost:5000
curl -X POST localhost:5000/api/links -H "Content-Type: application/json" -d "{\"url\":\"https://example.com/a/very/long/path\"}"
curl -i localhost:5000/<code>                 # 302
curl localhost:5000/api/links/<code>/stats    # analytics
```

## What happens in a run

```
requirements ─► design ─┬─► implementation ─┬─► verification ─┬─► release
                        └─► tests ──────────┴─► docs ─────────┘
```

1. **Plan**: the scenario's graph is built (`Planner`), validated (acyclic) and logged with its parallel waves.
   Brownfield/ambiguous runs start with a **codebase analysis** stage; the workspace is seeded with the previous
   scenario's output so it is a real existing code base.
2. **Schedule**: nodes whose dependencies have all succeeded run concurrently; joins are implicit.
3. **Execute a node**: the agent proposes (a requirement spec, a design with ADRs, a change set, a report). The engine
   then applies the **staleness gate**, the **policy gate** (secrets, dangerous APIs, dependency allow-list, protected
   paths, change-size classification), the **approval gate** (console, dashboard/API or `--auto-approve`, always
   audited with the actor), takes a **snapshot**, applies the change, records the **artifact** with an inputs hash and
   the **decision lineage**.
4. **Non-linear paths**: a failing verification re-runs implementation with the compiler/test output as feedback
   (bounded repair cycle); an agent can raise a **change request** that invalidates an upstream stage; a High-impact
   ambiguity pauses the run for a **human clarification**, after which the planner **re-plans** (adds an operational
   readiness gate, tightens approval).
5. **Stop safely**: exhausted budgets roll the workspace back to the pre-change baseline; a rejection, timeout, Ctrl+C,
   `STOP` file or API call halts with state persisted; `resume <runId>` continues.
6. **Observe**: `runs/<id>/audit.jsonl` (hash-chained), `state.json` (nodes, artifacts, decisions, metrics), per-run and
   aggregated metrics (success rate, retry/rollback frequency, MTTR, end-to-end latency).

## The three scenarios

| Scenario | Requirement | What it shows | Result (replay) |
|---|---|---|---|
| **Greenfield** | Build the URL shortener with aliases, expiry, health, SQLite, tests, README | Normalised spec with recorded assumptions; 5-wave graph with two parallel branches; policy gate; deterministic build+test gate; release approval | Completed · 40/40 tests · ≈18 s |
| **Brownfield** | Add click analytics without slowing redirects; keep existing tests green | Codebase impact analysis; minimal edits to existing files; approval on every applied change; **compile error caught by the gate and repaired in one bounded cycle**; rollback demo via `--inject-failure` | Completed · 44/44 · 1 repair · MTTR ≈15 s |
| **Ambiguous** | "Make the URL shortener more reliable." | **Clarification checkpoint** with options/defaults; **re-plan** adds an ops-readiness gate and tightens approval; **change request** rejects an infeasible Redis design; staleness gate; operability checks | Completed · 49/49 · 7/7 operability checks · 2 re-plans |

Each scenario document contains the decomposition, the orchestration trace and the validation evidence.

## Model modes

| Mode | Command | What it does |
|---|---|---|
| `replay` (default) | `--mode replay` | Deterministic. Agents receive recorded outputs from `fixtures/replay/<scenario>/<node>[.<attempt>].json`. No key needed. |
| `anthropic` | `--mode anthropic` | Live, via the official Anthropic SDK. Needs `ANTHROPIC_API_KEY`. Falls back to replay per node if the provider is unavailable (audited as `Fallback`). |
| `claude-cli` | `--mode claude-cli` | Live, by shelling out to a locally logged-in Claude Code CLI (`CLAUDE_CLI_PATH` or auto-discovered). |
| record | add `--record` to a live mode | Writes the live responses as new replay fixtures. |

The engine is identical in every mode. Recorded outputs were produced with an AI assistant during development and
curated so the demo is deterministic; two recordings intentionally contain realistic agent mistakes (a missing `using`,
an infeasible design) so the repair and change-request paths are exercised on every run.

## CLI reference

```
run <scenario> [--mode replay|anthropic|claude-cli] [--auto-approve] [--inject-failure <nodeId>] [--record]
resume <runId>                 continue a halted run (after rejection, safe-stop, timeout)
list | show <runId>            runs and full state
audit <runId> [--verify]       audit trail / hash-chain verification
metrics                        aggregated reliability metrics
scenarios                      scenario catalogue with graphs
serve [--port 5080]            HTTP API + dashboard
```

Environment: `AGENTIC_RUNS` (default `./runs`), `AGENTIC_FIXTURES` (default `./fixtures/replay`), `ANTHROPIC_API_KEY`, `CLAUDE_CLI_PATH`.

## Repository layout

```
src/AgenticSdlc.Core/        Model · Engine · Scenarios(Planner) · Agents · Policy · Approvals · Observability · Persistence · Workspace · Llm
src/AgenticSdlc.Host/        Program.cs (CLI + API) · Infrastructure.cs (RunManager, metrics aggregation, paths)
src/UrlShortener.Api/        the product: Program, Endpoints, Domain, Data, Analytics, Infrastructure, Contracts
tests/AgenticSdlc.Core.Tests, tests/UrlShortener.Api.Tests
fixtures/replay/<scenario>/  recorded agent outputs + files/ referenced by them
dashboard/                   Angular 22 UI
docs/                        scenarios, testing, engineering summary, talking points
run.ps1 / run.sh             one-command wrappers: test, scenario, serve, shortener
runs/                        (generated, git-ignored) state.json · audit.jsonl · workspace/ · snapshots/
```

## Limitations and trade-offs (short version)

Replay is the default for reproducibility; live modes are available but not covered by automated tests. The policy
engine is pattern-based change control, not SAST. Approvals are audited, not cryptographically signed. Rollback is a
directory snapshot. The shortener uses SQLite with `EnsureCreated` for zero-step local runs; migrations and PostgreSQL
are the documented production path; rate limiting is per instance. Full discussion in
[docs/ENGINEERING_SUMMARY.md](docs/ENGINEERING_SUMMARY.md).

## Setup troubleshooting

- `dotnet restore` hangs or fails on a corporate machine: the repo pins `nuget.config` to nuget.org only.
- Port 5080 or 5000 in use: `serve --port 5090`, `--urls http://localhost:5001`.
- `No replay fixture ... attempt N`: you ran a node past its recorded attempts in a custom scenario; add a fixture or run live with `--record`.
