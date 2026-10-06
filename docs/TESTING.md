# Testing approach

Two different things are under test: the **orchestrator** (does the governance machinery behave?) and the **product**
it builds (does the URL shortener work?). They are tested differently.

## 1. Orchestrator - `tests/AgenticSdlc.Core.Tests` (33 tests, ~5 s)

No model, no toolchain: agents are replaced by `ScriptedAgent`s that return scripted `StageResult`s, so each
governance path is exercised deterministically.

| Area | Tests |
|---|---|
| Happy path | all nodes succeed, success rate 1.0, release approval requested, audit chain verifies, artifacts == nodes |
| Parallelism | two agents are held in flight simultaneously → nodes in a wave really run concurrently |
| Retry | first attempt fails → second attempt receives the feedback → MTTR measured → `Retry` decision |
| Exhaustion + rollback | implementation fails 3×, design had written a file → status `RolledBack`, file gone, `Rollback` decision |
| Repair cycle | verification fails once → implementation re-runs with the failure text → workspace holds attempt 2 → old artifact superseded |
| Repair budget | verification always fails → exactly `MaxRepairCycles` repairs → rollback |
| Change request | implementation requests a design change → design attempt 2 gets the reason → `Replan` decision by `Actor.Agent` |
| Clarification | requirements asks a High-impact question → human answer recorded → `ops-readiness` node added → approval rule tightened |
| Rejection | reviewer rejects → run `Halted`, nothing applied, human `Reject` decision |
| Policy | hard-coded Anthropic key blocked (SEC-001) → findings fed back → second attempt clean → `PolicyBlock` decision |
| Safe-stop / resume | stop before start → `Halted`, 0 attempts → resume → `Completed`, `SafeStop` audit event |
| Failure injection | `--inject-failure design` → 3 attempts, 2 retries, `Failed` (nothing to roll back) |
| Persistence | reloaded state equals in-memory state |
| Graph | planner graphs valid with the expected parallel waves; cycles and dangling deps rejected; transitive dependents |
| Policy engine | secrets (4 patterns), dangerous APIs (service vs tests), dependency allow-list, protected paths, path escape, impact classification |
| Audit log | hash chain verifies; tampering one line is detected at the right sequence; reopening continues the chain |
| Workspace | path containment (`..`, absolute paths); snapshot/restore round trip |
| LLM plumbing | JSON extraction from fenced/chatty text; replay fixture resolution and `contentFile` expansion; fallback to replay when live is unavailable |

## 2. URL shortener - `tests/UrlShortener.Api.Tests` (49 tests, ~5 s)

Integration-first through `WebApplicationFactory<Program>`; every test class owns an isolated SQLite file, so classes
run in parallel.

| Class | Covers |
|---|---|
| `LinksApiTests` | create 201 + 7-char code, redirect 302, unknown 404, 7 invalid URL shapes → 400, alias honoured + duplicate 409, 4 invalid alias shapes, expiry 410, past expiry 400, metadata/delete lifecycle, health |
| `DomainTests` | Base62 generator (alphabet, length, uniqueness over 2000, range checks); URL validator accept/reject matrix incl. private IPv4/IPv6, credentials, length; alias rules |
| `StatsApiTests` | asynchronous click recording with referrer grouping, 7-day buckets, zero state, unknown 404, 20 redirects stay fast and all land |
| `ReliabilityTests` | readiness 200, correlation echo and generation, problem+json on validation failure |
| `RateLimitTests` | own factory with limit 3 → exactly 3×201 and 2×429; redirects unaffected |

Flakiness policy: tests that depend on time (expiry, async writer) poll with a bounded deadline rather than sleeping a
fixed amount. The verification gate in an early orchestrated run caught the original fixed-sleep version failing
under load, which is why the expiry test now polls.

## 3. End-to-end - the orchestrated scenarios themselves

Each `run <scenario>` is an end-to-end test: the verification stage builds and runs the **generated** workspace with
the real .NET toolchain. CI runs all three scenarios headless plus the failure-injection run and asserts:

| Scenario | Expected | Verified by |
|---|---|---|
| greenfield | `Completed`, 40/40 tests in the generated workspace | process exit code 0 |
| brownfield | `Completed` after 1 repair cycle, 44/44 | exit code 0 |
| ambiguous | `Completed` after clarification + change request, 49/49, 7/7 operability checks | exit code 0 |
| brownfield `--inject-failure verification` | `RolledBack` | exit code 1 and `"status": "RolledBack"` in `state.json` |
| every run | audit chain intact | `audit <id> --verify` |

## 4. Manual checks performed

- Console approval: rejecting at the prompt halts the run; `resume` re-runs the stage with the reviewer's comment as
  feedback and prompts again; approving completes the run.
- HTTP API: start a run, answer the clarification, approve implementation and release, verify the audit chain, read
  aggregated metrics (see `docs/scenarios/03-ambiguous.md`).
- Dashboard production build served by the host at `http://localhost:5080`.

## 5. What is not tested (known gaps)

- Live model modes (`anthropic`, `claude-cli`) have no automated tests; they share all agent/engine code with replay and
  were exercised manually only for connectivity.
- The Angular dashboard has no unit or end-to-end tests; it is verified by a strict production build and manual use.
- Concurrency of the shortener under real load (SQLite write contention) is covered only by the writer's retry logic
  and a light latency assertion, not by a load test.
