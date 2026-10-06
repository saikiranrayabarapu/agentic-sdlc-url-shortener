# Scenario 1 - Greenfield: build the URL shortener from an empty workspace

```bash
dotnet run --project src/AgenticSdlc.Host -- run greenfield            # interactive approval at release
dotnet run --project src/AgenticSdlc.Host -- run greenfield --auto-approve
```

## Requirement (as given to the system)

> Build a URL shortener HTTP service. Users submit a long URL and receive a short code; visiting the short URL redirects
> to the original. Support optional custom aliases and optional expiry. Expose a health endpoint. Reject invalid or
> non-http(s) URLs. Store links durably in SQLite. Include automated tests and a README.

## Requirement understanding

The Requirements agent normalises this into 8 functional requirements, 5 non-functional requirements and 8 testable
acceptance criteria (`fixtures/replay/greenfield/requirements.json`). It records two Low-impact ambiguities with
default assumptions instead of blocking:

| Id | Ambiguity | Default taken |
|---|---|---|
| Q1 | Same URL → same code, or a new code per request? | New code per request (does not leak that a URL was already shortened) |
| Q2 | 301 or 302 redirect? | 302, so future analytics see every click |

Because no ambiguity is High impact, `needsClarification` is false and the run proceeds without a human checkpoint.

## Decomposition (dependency graph)

```
wave 1  requirements
wave 2  design
wave 3  implementation ∥ tests          (both depend on design only)
wave 4  verification ∥ docs             (verification joins implementation + tests)
wave 5  release                         (joins verification + docs; approval always required)
```

Design produces four recorded decisions (ADR-1 random Base62 codes with collision retry, ADR-2 SQLite + EnsureCreated,
ADR-3 302 redirects, ADR-4 block private hosts), an API contract, a data model and a 13-file plan.

## Orchestration trace (abridged from `audit.jsonl`)

```
WorkflowPlanned      7 nodes in 5 waves
requirements  a1     NodeSucceeded  (RequirementSpec)
design        a1     NodeSucceeded  (DesignDoc)
implementation a1    PolicyCheck 8 rules on 9 files: pass, impact Medium → Snapshot → WorkspaceWrite 9 files
tests         a1     PolicyCheck 8 rules on 4 files: pass, impact Low    → Snapshot → WorkspaceWrite 4 files
verification  a1     ToolRun dotnet build ✓ · ToolRun dotnet test ✓ → "Build passed; 40/40 tests passed"
docs          a1     WorkspaceWrite README.md, CHANGELOG.md
release       a1     ApprovalRequested (impact High) → ApprovalDecided → NodeSucceeded (ReleaseReport: Approve)
Metrics              success rate 100%, retries 0, repairs 0, rollbacks 0, approvals 1/1, e2e ≈ 18 s
```

`implementation` has approval rule *IfHighImpact*; the policy engine classified the change as Medium (new files only,
greenfield), so no approval was needed there. `release` always requires a human.

## Validation

| Gate | Evidence |
|---|---|
| Requirements exit gate | ≥ 3 testable acceptance criteria present |
| Design exit gate | decisions with trade-offs and a file plan present; dependencies on the approved list |
| Policy gate | SEC-001/002/003, CMP-001/002, CHG-001/002/003 evaluated on every change set; 0 blocking findings |
| Verification exit gate (deterministic) | `dotnet build` and `dotnet test` on the generated workspace: 40 tests, 0 failures |
| Release gate | checklist, risk register, rollback plan, human sign-off |
| Audit | `audit <runId> --verify` → chain intact |

## Output

The workspace `runs/<runId>/workspace/` contains a complete, independently runnable solution:
`UrlShortener.Api/` (minimal API, EF Core SQLite), `UrlShortener.Api.Tests/` (xUnit + WebApplicationFactory),
`README.md`, `CHANGELOG.md`. The committed `src/UrlShortener.Api` is the evolved result after scenarios 2 and 3.

## What this scenario demonstrates

Requirement normalisation with explicit assumptions, an explicit graph with two parallel branches and a join,
policy gating before any write, a deterministic exit gate, a human release approval, and a complete artifact lineage.
