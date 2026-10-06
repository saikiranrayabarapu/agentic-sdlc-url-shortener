# Talking points for the interview

Read this before the discussion. Each item is a question you are likely to be asked, followed by the answer in the
words you would use, and where in the repo the proof lives.

## The one-minute pitch

"I built two things. First, an orchestration engine in C# that takes a requirement and drives it through requirements,
design, implementation, tests, verification, docs and release as an explicit dependency graph, with gates, human
approvals, bounded retries, rollback and an audit trail. Second, a URL shortener that the engine builds and then
evolves across three scenarios: from scratch, adding analytics to the existing code, and a deliberately vague
'make it more reliable' request. The engine is tested on its own (33 tests), the shortener on its own (49 tests),
and each scenario runs end to end from a fresh clone, building and testing the generated code with the real .NET
toolchain."

## "Walk me through the architecture."

Start at `ARCHITECTURE.md` section 1. Order: hosts (CLI, API, dashboard) → approval boundary → engine → agents →
LLM clients → workspace and store. Key sentence: **agents propose, the engine applies**. Nothing an agent produces
touches the workspace until the policy gate and, where required, a human approval have passed, and a snapshot has been
taken.

## "Why not a linear chain / why not LangGraph, Temporal, Semantic Kernel...?"

"The exercise evaluates governance: gates, re-planning, lineage, rollback. Those are exactly the parts a workflow
framework hides behind its own abstractions. A small scheduler over a graph that is plain data kept every transition
visible, auditable and unit-testable. If this were going to production I would evaluate hosting it on a durable
execution engine for persistence and scale, but I would keep the governance model."

## "Show me something non-linear."

Three paths, all in `src/AgenticSdlc.Core/Engine/Orchestrator.cs`:

1. **Repair cycle** (`HandleFailureAsync`): when verification fails, it does not retry itself. It invalidates
   `implementation`, feeds the compiler/test output in as feedback and re-runs it. Proof: brownfield run, `CS0246`
   missing `using`, fixed on attempt 2. Budget: `MaxRepairCycles = 2`.
2. **Change request** (`HandleChangeRequest`): a downstream agent can reject an upstream artifact. Proof: ambiguous run,
   implementation rejects the Redis design, design re-runs. Budget: `MaxReplans`.
3. **Clarification re-plan** (`HandleClarificationAsync` + `Planner.ReplanAfterClarification`): the graph gains a node
   (`ops-readiness`) and the approval rule tightens while the run is in flight.

Plus the **staleness gate** in `HandleSuccessAsync`: if an upstream node was invalidated while this node ran, the result
is discarded and the node re-runs.

## "How do you keep the agent from doing damage?"

- Path containment in `Workspace.Resolve` (no `..`, no absolute paths).
- `PolicyEngine`: secrets, dangerous APIs, dependency allow-list, protected paths. Blocking findings never reach the
  workspace and are fed back to the agent.
- Approval rules per node: `Always` for release and for brownfield implementation; `IfHighImpact` otherwise, where
  impact is the max of what the agent declared and what the policy engine computed (deletions, many files, edits to
  existing files raise it).
- Budgets everywhere: retries, repair cycles, re-plans, max run duration.
- Snapshot before every apply; rollback to the first snapshot on exhaustion.
- Safe-stop from the console, a `STOP` file or the API; state is persisted after every transition; `resume` continues.

## "What is audit-grade about the audit log?"

"Every event is a JSON line with a sequence number, actor, node, attempt, structured data and a SHA-256 over the event
plus the previous hash. `audit <runId> --verify` recomputes the chain. There is a unit test that edits one line and
shows detection at the right sequence number." (`Observability/AuditLog.cs`, `ComponentTests.AuditLogTests`.)

## "Which metrics do you track and how is MTTR computed?"

Per run: nodes attempted/succeeded/failed, success rate, retries, repair cycles, rollbacks, fallbacks, re-plans, policy
violations, approvals requested/granted, model calls, end-to-end latency, per-stage latency. MTTR is the mean of
failure→recovery windows: a window opens when an attempt fails and closes when the same node (or, for repairs, the
verification node) next succeeds. Aggregates across runs (`metrics` command, `/api/metrics`): run and node success
rate, retry and rollback frequency per run, mean MTTR, end-to-end avg/p50/p95, by status and by scenario.

## "Is this really agentic if it replays fixtures?"

Be direct: "By default it replays recorded agent outputs so a reviewer can clone and run it with no API key. The same
agent code, prompts and JSON contracts run live with `--mode anthropic` or `--mode claude-cli`, and `--record` writes
new fixtures. The recordings were produced with an AI assistant during development and curated so the demo is
deterministic. The engine does not know which mode it is in; everything it does — gates, approvals, repair, rollback —
is identical. If a live run produces worse code, that is exactly what the verification gate and repair loop exist for."

## "Where did the model actually get it wrong, and what happened?"

Two recorded cases, both intentional and both representative of real failures:

- Brownfield: the first implementation forgot `using System.Threading.Channels;`. The build gate caught it; the
  compiler error was the feedback; the second attempt fixed it; the fix was approved again before applying.
- Ambiguous: the first design proposed Redis. The implementation stage raised a change request because it violated the
  clarified constraint and the dependency allow-list. The design was redone in-process.

Also a genuine one found during development: the verification gate failed a greenfield run because the expiry test used
a fixed sleep and flaked under load. The test now polls with a deadline (`docs/TESTING.md` section 2).

## "Tell me about the URL shortener design decisions."

- Random 7-character Base62 codes from a cryptographic RNG, collision handled by the unique index + bounded retry
  (not sequential ids: enumerable; not hashes: deterministic and leaky).
- 302 not 301 so analytics see every click and expiry works.
- Block loopback/private hosts by default (a public redirector is otherwise an SSRF/phishing helper).
- Analytics are asynchronous through a bounded `Channel` and a `BackgroundService` that batches writes, retries SQLite
  locks and drains on shutdown. Redirect latency does not depend on the write.
- Click timestamps are UTC `DateTime` because the SQLite EF provider cannot compare or aggregate `DateTimeOffset`.
- Reliability slice: built-in fixed-window rate limiter on create only, readiness probe separate from liveness,
  correlation id middleware, problem details everywhere. Per-instance limits are a documented limitation.
- SQLite + `EnsureCreated` for zero-step local runs; migrations and PostgreSQL are the documented production path.

## "What would you do next?"

1. Durable execution host (persist the scheduler itself, not just state files) and a proper event stream for the UI.
2. Signed approvals and role separation between proposer, approver and releaser.
3. Live-mode evaluation harness: run each scenario live N times, measure gate catch rate and repair success.
4. Replace pattern-based policy with SAST/SCA integration as an additional gate.
5. EF migrations and PostgreSQL option for the shortener; distributed rate limiting when scaling out.

## Numbers worth remembering

| | |
|---|---|
| Engine tests | 33 |
| Shortener tests | 49 (40 after greenfield, 44 after brownfield, 49 after ambiguous) |
| Stages in the graph | 7 (greenfield), 8 (brownfield), 9 after the ambiguous re-plan |
| Policy rules | 8 (SEC-001..003, CMP-001..002, CHG-001..003) |
| Replay run time | about 20 s per scenario, dominated by `dotnet build` + `dotnet test` of the generated code |
| Brownfield run metrics | success rate 91%, 1 repair cycle, MTTR ≈ 15 s, approvals 3/3 |

## Honest limitations to volunteer before being asked

Replay default; live modes untested automatically; dashboard has no tests; policy engine is pattern-based; approvals
are recorded, not signed; rollback is directory-level. All listed in `docs/ENGINEERING_SUMMARY.md`.
