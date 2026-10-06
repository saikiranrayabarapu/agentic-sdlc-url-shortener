# Scenario 3 - Ambiguous: "Make the URL shortener more reliable."

```bash
dotnet run --project src/AgenticSdlc.Host -- run ambiguous              # you answer the clarification + approve
dotnet run --project src/AgenticSdlc.Host -- run ambiguous --auto-approve   # defaults are taken, audited as automated
```

The workspace is seeded with the outputs of scenarios 1 and 2 (shortener with analytics, version 1.1).

## Requirement

> Make the URL shortener more reliable.

That is the whole requirement. There is no definition of "reliable", no target and no constraint.

## Requirement understanding: detect, do not guess

The `CodebaseAnalysis` stage establishes what already exists (durable storage, validation, async analytics, liveness
probe) and what is missing (abuse protection, readiness signal, correlation, migrations). The `Requirements` agent then
refuses to normalise the requirement silently. Its first attempt returns `needsClarification = true` with two
High-impact and one Medium-impact ambiguities, each with options and a default:

| Id | Question | Options | Default |
|---|---|---|---|
| Q1 (High) | What does "reliable" mean here? | operational resilience / data durability / high availability | operational resilience: rate limiting, health + readiness probes, graceful shutdown, consistent problem responses, correlation ids |
| Q2 (High) | May new infrastructure be introduced (Redis, PostgreSQL, broker)? | no / yes | no: in-process only, approved packages only |
| Q3 (Medium) | Is there a measurable target? | none / specify | sensible configurable defaults |

The orchestrator parks the run in `AwaitingClarification` and asks the human (console options, dashboard radios, or
`POST /api/runs/{id}/clarifications/{id}`). Answers are recorded as a human `Clarify` decision and become part of every
downstream prompt and of the inputs hash of every downstream artifact.

## Dynamic re-planning (1): after clarification

`Planner.ReplanAfterClarification` mutates the live graph:

```
WorkflowReplanned  Workflow re-planned after clarification (2 change(s))
  implementation.approval: IfHighImpact -> Always   (requirement was ambiguous; human must confirm scope before changes apply)
  + node ops-readiness (OperationalReadiness) after implementation/tests; release now depends on it
```

The `requirements` node then re-runs (attempt 2) with the answers and produces the real specification: 5 functional
requirements, 4 non-functional requirements, 6 acceptance criteria, scope exclusions.

## Dynamic re-planning (2): a change request from downstream

`design` (attempt 1) proposes a **Redis-backed** distributed rate limiter (ADR-8). `implementation` (attempt 1) does
not blindly implement it. It returns a **change request**:

> Design ADR-8 depends on Redis (external infrastructure + non-approved package StackExchange.Redis), which contradicts
> clarified constraint Q2 and would be blocked by policy CMP-001. Please redesign with the in-process ASP.NET Core rate
> limiter.

The orchestrator validates that `design` is upstream of `implementation`, checks the re-plan budget, supersedes the
design artifact, invalidates `design` and its dependents, records an agent `Replan` decision, and re-runs `design`
with the change request as feedback. Design attempt 2 (ADR-8 revised, ADR-9 liveness vs readiness, ADR-10 custom
correlation middleware) has no external dependencies. The `tests` stage, which was already running against the
superseded design, is caught by the **staleness gate** and re-runs with the new inputs.

## Orchestration trace

```
analysis a1          NodeSucceeded
requirements a1      ClarificationRequested (2 high-impact questions) → ClarificationAnswered → WorkflowReplanned (+ops-readiness, approval tightened)
requirements a2      NodeSucceeded (final spec)
design a1            NodeSucceeded (Redis-based)
implementation a1    WorkflowReplanned: change request on 'design' → design invalidated (re-plan 2/3)
design a2            NodeSucceeded (in-process)
tests a2 ∥ implementation a2
implementation a2    PolicyCheck 4 files: pass, impact Medium → ApprovalRequested → approved → Snapshot → apply
verification a1 ∥ ops-readiness a1 ∥ docs a1
ops-readiness a1     "All 7 operability checks passed" (health, readiness, rate limiter, graceful shutdown, problem details, correlation id, run docs)
verification a1      dotnet build ✓ · dotnet test ✓ → 49/49
release a1           ApprovalRequested (High) → approved → ReleaseReport: Approve
Metrics              success rate 83%, re-plans 2, approvals 2/2, e2e ≈ 21 s
```

Decision lineage of the run: `Human Clarify (requirements)` → `System Replan (planner)` → `Agent Replan (design)` →
`Human Approve (implementation)` → `Human Approve (release)`.

## Validation

| Check | Evidence |
|---|---|
| Ambiguity surfaced instead of assumed | `ClarificationRequested` with options and defaults; run paused |
| Clarification is traceable | answers in `RunState.ClarificationAnswers`, in the audit log and in the `Clarify` decision; final spec cites them |
| Plan adapted to clarified scope | `ops-readiness` node added at run time; `release` depends on it |
| Governance tightened | implementation approval became mandatory after clarification |
| Infeasible design rejected before any code was written | change request path; policy CMP-001 would also have blocked `StackExchange.Redis` |
| Scope delivered | `ReliabilityTests` + `RateLimitTests` (3×201, 2×429, redirects unthrottled, readiness, correlation echo/generation, problem+json) |
| Operability verified independently of the model | deterministic `OperationalReadinessAgent` checks |

## Human-in-the-loop via the API (as exercised)

```
POST /api/runs {scenario: ambiguous}          → status AwaitingClarification, pendingClarification.questions = [Q1, Q2]
POST /api/runs/{id}/clarifications/{cid}      → 202; status AwaitingApproval, pendingApprovals = [implementation-a2 (Medium)]
POST /api/runs/{id}/approvals/implementation-a2 {approved: true, approver, comment} → 202
POST /api/runs/{id}/approvals/release-a1       → 202; status Completed
GET  /api/runs/{id}/audit/verify              → { ok: true, events: 78 }
```

## What this scenario demonstrates

Ambiguity detection with a hard stop for a human decision, recorded assumptions, two distinct re-planning mechanisms
(clarification-driven graph mutation and downstream change requests), bounded re-plan budget, a staleness gate for
in-flight work, and an operability gate added only because the clarified scope asked for it.
