# Architecture

This document describes the agentic orchestration layer, how it drives a software change through the SDLC, and the
key decisions behind it. The URL shortener it builds is described in its own README inside the generated workspace
and in [src/UrlShortener.Api](src/UrlShortener.Api).

## 1. Components

```
┌────────────────────────────────────────────────────────────────────────────────────┐
│  Hosts                                                                              │
│  ┌──────────────────────────┐   ┌──────────────────────────┐   ┌─────────────────┐  │
│  │ CLI (run/resume/audit/   │   │ HTTP API (/api/runs,     │   │ Angular         │  │
│  │ metrics/list/show)       │   │ approvals, audit, stream)│◄──│ dashboard       │  │
│  └─────────────┬────────────┘   └─────────────┬────────────┘   └─────────────────┘  │
│                │                               │                                     │
│  Human-in-the-loop boundary: IApprovalProvider (Console | Queued (API) | Auto)      │
├────────────────┴───────────────────────────────┴─────────────────────────────────────┤
│  Orchestrator (engine)                                                              │
│   • scheduler over an explicit dependency graph (waves of ready nodes run in       │
│     parallel, joins are implicit in DependsOn)                                      │
│   • entry gates / exit gates per node                                               │
│   • bounded retries, cross-node repair cycle, change requests → invalidation        │
│   • clarification checkpoint → Planner.Replan (graph mutates at run time)          │
│   • policy engine gate → human approval gate → snapshot → apply                     │
│   • rollback to baseline, safe-stop, resume from persisted state                    │
│   • audit log (hash chain), decision lineage, metrics                               │
├──────────────────────────────────────────────────────────────────────────────────────┤
│  Agents (propose, never apply)                 │  Deterministic agents (evidence)    │
│   Requirements · CodebaseAnalysis · Design ·   │   Verification (dotnet build/test)  │
│   Implementation · TestAuthoring ·             │   OperationalReadiness (static      │
│   Documentation · ReleaseReadiness             │   operability checks)               │
├────────────────────────────────────────────────┴─────────────────────────────────────┤
│  LLM clients: Replay (recorded, default) · Anthropic SDK · Claude CLI · Recording ·  │
│  Fallback (live → replay)                                                            │
├──────────────────────────────────────────────────────────────────────────────────────┤
│  Workspace (sandboxed dir, path-contained, snapshots)   RunStore (state.json, audit) │
└──────────────────────────────────────────────────────────────────────────────────────┘
```

| Component | Location | Responsibility |
|---|---|---|
| Model | `src/AgenticSdlc.Core/Model` | `WorkflowDefinition`/`StageNode` (the graph), `RunState`/`NodeState`, `Artifact`, `Decision`, approvals, metrics |
| Engine | `src/AgenticSdlc.Core/Engine/Orchestrator.cs` | Owns every state transition. Agents are called; the engine gates, approves, applies, retries, repairs, rolls back, re-plans, audits, measures |
| Planner | `src/AgenticSdlc.Core/Scenarios/Scenarios.cs` | Builds the initial graph per scenario kind and re-plans after clarification |
| Agents | `src/AgenticSdlc.Core/Agents` | One agent per `StageKind`. Model-backed agents share `LlmStageAgent` (prompt assembly, JSON contract parsing). Verification and operational readiness are deterministic |
| Policy | `src/AgenticSdlc.Core/Policy/PolicyEngine.cs` | Guardrails evaluated before any change touches the workspace |
| Approvals | `src/AgenticSdlc.Core/Approvals` | `IApprovalProvider` implementations: console, queued (API/dashboard), automatic (CI/demo, audited as automated) |
| Observability | `src/AgenticSdlc.Core/Observability/AuditLog.cs` | Append-only JSONL with SHA-256 hash chain |
| Persistence | `src/AgenticSdlc.Core/Persistence/RunStore.cs` | `runs/<runId>/state.json`, `audit.jsonl`, `workspace/`, `snapshots/`, `STOP` |
| Workspace | `src/AgenticSdlc.Core/Workspace/Workspace.cs` | Sandboxed directory; path containment; snapshot/restore |
| LLM | `src/AgenticSdlc.Core/Llm` | Provider abstraction and the replay/record mechanism |
| Host | `src/AgenticSdlc.Host` | CLI + HTTP API, serves the dashboard build |
| Dashboard | `dashboard/` | Angular UI: runs, stage graph, approvals, clarifications, lineage, audit, metrics, workspace browser |

## 2. Orchestration model

### 2.1 The graph is data

A run starts from a `WorkflowDefinition`: a list of `StageNode`s with `DependsOn`, `MaxRetries`, an `Approval` rule
(`None | IfHighImpact | Always`) and human-readable entry/exit gate descriptions. The graph is validated (unique ids,
known dependencies, acyclic via Kahn's algorithm) and its topological *waves* are logged at plan time. Because the graph
is data held in `RunState`, it can be mutated during the run (re-planning) and inspected afterwards.

Initial graph (brownfield/ambiguous add `analysis` in front of `requirements`):

```
                      ┌─► implementation ─┐
requirements ─► design┤                   ├─► verification ─┐
                      └─► tests ──────────┤                 ├─► release
                                          └─► docs ─────────┘
```

Wave 1 `requirements` · Wave 2 `design` · Wave 3 `implementation ∥ tests` · Wave 4 `verification ∥ docs` · Wave 5 `release`.
After a clarification that includes operability scope, the planner inserts `ops-readiness` into wave 4 and makes
`release` depend on it.

### 2.2 Scheduler

```
loop:
  if stop requested or max duration exceeded → Halt (persist, resumable)
  ready = nodes whose status ∈ {Pending, Invalidated} and whose dependencies are all Succeeded
  if none ready: all Succeeded → Complete; otherwise a terminal status was already set (or the graph is stuck → Fail)
  log EntryGate for each ready node
  run ready nodes concurrently (Task.Run per node, SemaphoreSlim bound) and await all
```

Synchronisation is implicit: `verification` cannot become ready until both `implementation` and `tests` have
succeeded. Parallelism is real (verified by a unit test that holds two agents in flight simultaneously).

### 2.3 Node execution

```
attempt loop:
  attempts++ ; status=Running ; persist ; audit NodeStarted (with feedback from the previous failure)
  result = agent.Execute(context)            (or an injected failure when --inject-failure names this node)
  switch result:
    ClarificationNeeded → park run (AwaitingClarification) → ask IApprovalProvider → record answers +
                          decision(Clarify) → Planner.Replan → audit WorkflowReplanned → re-run node
    ChangeRequest       → validate target is upstream & re-plan budget left → supersede artifacts →
                          Invalidate(target + dependents) → decision(Replan) → hand back to scheduler
    Failed              → Verification? → repair cycle (bounded): invalidate implementation with feedback
                          else attempt ≤ MaxRetries → Retry with feedback
                          else → RollbackAndStop (restore first snapshot, status RolledBack)
    Succeeded           → staleness gate (upstream invalidated meanwhile? discard & re-run)
                          → policy gate (block → treated as failure with findings as feedback)
                          → approval gate (Always / IfHighImpact, impact = max(agent, policy))
                          → snapshot → apply change set → record artifact (+inputs hash) → ExitGate → Succeeded
```

Three non-linear paths exist by construction: the **repair cycle** (verification failure re-runs implementation),
**change requests** (a downstream agent invalidates an upstream artifact), and **clarification-driven re-planning**
(the graph gains nodes and tighter approval rules while running).

### 2.4 Context and decision lineage

Every stage output is an `Artifact` with `NodeId`, `Attempt`, `Type`, `Summary`, JSON `Content` and an `InputsHash`
(SHA-256 over the requirement, upstream artifact ids+hashes, clarification answers and feedback). When upstream changes,
the hash changes and old artifacts are flagged `Superseded`. Downstream agents receive upstream artifacts through
`StageContext.UpstreamJson(kind)`. Every governance event (approve, reject, retry, repair, rollback, replan, clarify,
policy block, safe-stop) is a `Decision` with actor, rationale and the artifact ids it was based on.

## 3. Governance

### 3.1 Human approval checkpoints

| Checkpoint | When | Provider behaviour |
|---|---|---|
| Change-set approval | `implementation` when rule is `Always` (brownfield; ambiguous after clarification) or when impact is High | Console prompt / dashboard card / automatic (audited as `Actor=System`, approver "auto-approve flag") |
| Release approval | `release` always | same |
| Clarification | `requirements` returns High-impact ambiguities | Console numbered options / dashboard radios / defaults |

A rejection halts the run safely (nothing is applied) and is recorded as a human `Reject` decision. Rejected or
timed-out runs are resumable.

### 3.2 Policy guardrails (`PolicyEngine`)

| Rule | Severity | What it catches |
|---|---|---|
| SEC-001 | Block | Hard-coded secrets (AWS keys, private keys, credential literals, Anthropic/GitHub tokens) |
| SEC-002 | Block | Dangerous APIs in service code (`Process.Start`, `DllImport`, `Assembly.Load*`, `Environment.Exit`, TLS validation disabled) |
| SEC-003 | Warn | Wide-open CORS |
| CMP-001 | Block | NuGet packages outside the approved prefix list |
| CMP-002 | Block | Writes to protected paths (CI, package sources, production config) or outside the workspace |
| CHG-001/002/003 | Impact | Large change sets, deletions, edits to existing files raise impact → triggers human approval |

Blocked change sets never touch the workspace; the findings are fed back to the agent as the next attempt's input.

### 3.3 Reliability controls

| Control | Implementation |
|---|---|
| Bounded retries | `StageNode.MaxRetries` per node, with feedback injection |
| Repair cycle | `WorkflowDefinition.MaxRepairCycles` (verification → implementation) |
| Re-plan budget | `WorkflowDefinition.MaxReplans` (change requests + clarification re-plans) |
| Fallback | `FallbackLlmClient`: live provider unavailable → replay fixture, audited as `Fallback` |
| Rollback | Snapshot before every apply; on exhaustion restore the first snapshot (pre-change baseline) |
| Safe-stop | `STOP` file / API / Ctrl+C checked between steps; state persisted; `resume <runId>` continues |
| Staleness gate | A result computed from inputs invalidated mid-flight is discarded and the node re-runs |
| Time box | `OrchestratorOptions.MaxRunDuration` |
| Engine errors | An exception inside the engine after an agent returns (persistence, snapshot, apply) halts the run safely (`SafeStop`, node back to `Pending`, resumable) instead of crashing the process |
| Persistence | `state.json` is written to a temp file and atomically replaced; the replace retries briefly when another process holds the file open (Windows antivirus/indexer collisions) |

## 4. Observability

- **Audit log**: `runs/<id>/audit.jsonl`, one JSON object per event with `seq`, timestamp, run/node/attempt, type, actor,
  message, structured data, `prevHash`, `hash`. `audit <id> --verify` (or `GET /api/runs/{id}/audit/verify`) re-computes
  the chain; a unit test tampers with a line and proves detection.
- **Metrics per run** (`RunMetrics`): nodes attempted/succeeded/failed, success rate, retries, repair cycles, rollbacks,
  fallbacks, re-plans, policy violations, approvals requested/granted, model calls, end-to-end latency, per-stage latency,
  and MTTR computed from failure→recovery windows.
- **Aggregated metrics** (`metrics` command / `GET /api/metrics`): run success rate, node success rate, retry and
  rollback frequency per run, mean MTTR, end-to-end latency avg/p50/p95, breakdown by status and scenario.
- **Dashboard**: live stage graph by wave, approvals and clarifications, lineage, artifacts, audit tail, workspace browser.

## 5. Key decisions

| # | Decision | Alternatives | Why |
|---|---|---|---|
| 1 | Explicit graph as data + scheduler, not a linear pipeline or a general workflow engine | Linear chain; Temporal/Durable Functions | The assignment asks for inspectable gates, re-planning and lineage. A small bespoke scheduler makes every transition visible and testable; a workflow engine would hide the governance logic the exercise is about |
| 2 | Agents propose, the engine applies | Agents writing files directly | Separation of duties: policy and approval gates sit between proposal and effect, so nothing unreviewed reaches the workspace |
| 3 | Deterministic verification (real `dotnet build`/`dotnet test`) as the exit gate | Model self-assessment | Evidence cannot be hallucinated; the repair loop is driven by compiler/test output |
| 4 | Replay mode as default, live modes optional | Live model required | A reviewer can clone and run everything with no key; the same agent code runs live with `--mode anthropic` or `--mode claude-cli`, and `--record` captures new fixtures |
| 5 | Verification failure repairs upstream (implementation), not itself | Retry verification | Re-running a failing build changes nothing; the fix must come from the producer with the evidence as feedback |
| 6 | Hash-chained JSONL audit | Plain log lines | Tamper-evidence is the difference between a log and an audit trail |
| 7 | Snapshot/restore of a sandboxed directory | Git-based rollback | No dependency on git state inside the workspace; simple, testable, and sufficient for reversibility |
| 8 | Approval rule tightens after clarification | Static rules | An ambiguous requirement means the plan was made with incomplete information; once clarified, humans must confirm the scope before changes apply |
| 9 | Policy allow-list for dependencies | Deny-list | Change control in regulated environments is allow-list based; the ambiguous scenario shows the gate rejecting a Redis-based design |

## 6. Control flow of a brownfield run (as executed)

```
analysis ─► requirements ─► design ─► implementation(a1) ─[policy ✓, approval ✓, snapshot, apply]
                                   └► tests(a1) ──────────[policy ✓, snapshot, apply]
          verification(a1): dotnet build ✗ (CS0246) ──► RepairCycle 1/2 ─► implementation(a2) with compiler errors as feedback
                                                                 └─[policy ✓, approval ✓, snapshot, apply]
          docs(a2) ∥ verification(a2): build ✓, 44/44 tests ✓ ─► release ─[approval ✓]─► Completed
metrics: success rate 91%, repairs 1, MTTR 15s, approvals 3/3, e2e ~23s
```

## 7. Extending

- **New stage**: implement `IStageAgent` (or derive from `LlmStageAgent` with a JSON contract), add a `StageKind`, register
  in `AgentRegistry.Default()`, add a `StageNode` in `Planner`.
- **New policy rule**: add to `PolicyEngine.EvaluateChangeSet` with a rule id and severity; it is automatically audited.
- **New provider**: implement `ILlmClient`; wrap with `RecordingLlmClient`/`FallbackLlmClient` in `LlmClientFactory`.
- **New scenario**: add a `ScenarioDefinition` and a `fixtures/replay/<id>/` folder (or run live with `--record`).
