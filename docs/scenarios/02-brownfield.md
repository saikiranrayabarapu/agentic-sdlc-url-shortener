# Scenario 2 - Brownfield: add click analytics to the existing shortener

```bash
dotnet run --project src/AgenticSdlc.Host -- run brownfield            # approves each change set interactively
dotnet run --project src/AgenticSdlc.Host -- run brownfield --auto-approve
dotnet run --project src/AgenticSdlc.Host -- run brownfield --auto-approve --inject-failure verification   # rollback demo
```

The workspace is **seeded with the greenfield output** (the recorded implementation, tests and docs of scenario 1), so
the run starts from a real, existing code base.

## Requirement

> Add click analytics to the existing URL shortener. Every redirect must record a click (timestamp, referrer,
> user-agent) without slowing down the redirect. Expose GET /api/links/{code}/stats returning total clicks, clicks per
> day for the last 7 days, and the top 5 referrers. Existing behaviour and tests must keep working.

## Codebase reasoning

A `CodebaseAnalysis` stage runs first. It combines a **deterministic inventory** (file list, endpoints found by scanning
`Map{Get,Post,...}` calls, types, NuGet packages) with the model's impact reasoning. The resulting `ImpactAnalysis`
artifact names the impacted modules and data flows:

| Module | Change | Why |
|---|---|---|
| `Endpoints/LinkEndpoints.cs` | modify | `RedirectAsync` must enqueue a click; new `GetStatsAsync` |
| `Data/AppDbContext.cs` | modify | `DbSet<ClickEvent>`, index on (LinkId, OccurredAtUtc), cascade FK |
| `Program.cs` | modify | register `ClickQueue` + `ClickRecorder` |
| `Contracts/Dtos.cs` | modify | stats response records |
| `Domain/ClickEvent.cs`, `Analytics/ClickQueue.cs`, `Analytics/ClickRecorder.cs` | new | entity, bounded channel, background batch writer |
| `UrlShortener.Api.Tests/StatsApiTests.cs` | new | acceptance tests |

It also flags two risks that shaped the design: the EF Core SQLite provider cannot compare or aggregate
`DateTimeOffset` (so the click timestamp is a UTC `DateTime`), and `EnsureCreated` will not add the new table to an
existing database.

## Decomposition

```
wave 1  analysis
wave 2  requirements
wave 3  design                    (ADR-5 async bounded queue, ADR-6 UTC DateTime, ADR-7 in-memory 7-day projection)
wave 4  implementation ∥ tests    (implementation approval rule: Always - brownfield edits existing files)
wave 5  verification ∥ docs
wave 6  release
```

## Orchestration trace - including a real failure and repair

```
analysis a1        NodeSucceeded (ImpactAnalysis, 8 impacted modules)
requirements a1    NodeSucceeded
design a1          NodeSucceeded
tests a1           PolicyCheck pass → apply 1 file
implementation a1  PolicyCheck 7 files: pass, impact Medium (CHG-003: existing files modified)
                   ApprovalRequested → ApprovalDecided (approved) → Snapshot → apply 7 files
docs a1            apply README.md, CHANGELOG.md
verification a1    dotnet build ✗  → NodeFailed "Build failed with 1 error(s)"
                     error CS0246: The type or namespace name 'Channel<>' could not be found
RepairCycle 1/2    re-running 'implementation' with the compiler output as feedback
implementation a2  (feedback: "Fix these compiler errors: ... CS0246 ...")
                   PolicyCheck pass → ApprovalRequested → approved → Snapshot → apply 7 files
                   summary: "Repaired ... ClickQueue was missing `using System.Threading.Channels;`"
docs a2            re-run because it depends on implementation (its attempt-1 artifact is superseded)
verification a2    dotnet build ✓ · dotnet test ✓ → "Build passed; 44/44 tests passed"
release a1         ApprovalRequested (High) → approved → ReleaseReport: Approve
Metrics            success rate 91%, repairs 1, rollbacks 0, approvals 3/3, MTTR ≈ 15 s, e2e ≈ 23 s
```

Points to notice:

- The **repair path is non-linear**: a failure in `verification` does not retry `verification`; it invalidates
  `implementation`, supersedes its artifact, feeds the compiler output into the next attempt and re-runs the dependent
  `docs` node too.
- **Every applied change set was approved**, including the repaired one. Each apply is preceded by a snapshot.
- The regression gate is the pre-existing test suite: 40 old tests + 4 new ones all pass.

## Failure injection: rollback and safe stop

`--inject-failure verification` makes the verification stage fail on every attempt:

```
RepairCycle 1/2 → implementation a2 → verification ✗
RepairCycle 2/2 → implementation a3 → verification ✗
Warning  Repair budget exhausted
Rollback Workspace rolled back to snapshot '<first snapshot>'   ← the seeded v1 baseline, no analytics files remain
Run RolledBack: Verification still failing after 2 repair cycles        (process exit code 1)
```

## Validation

| Check | Evidence |
|---|---|
| Impact analysis vs actual change | 7 files changed == impacted modules (tests excluded) |
| Existing behaviour preserved | all greenfield tests still pass unchanged |
| Redirect latency independent of analytics | `StatsApiTests.Redirect_latency_does_not_wait_for_analytics_write` |
| Eventual consistency bounded | tests poll stats with a 10 s bound |
| Change control | approval rule Always; CHG-003 raised impact to Medium; both attempts approved |
| Reversibility | injected-failure run restores the baseline snapshot |

## What this scenario demonstrates

Codebase reasoning before requirements, minimal modification of existing files, a deterministic gate catching a real
compile error, bounded repair with evidence as feedback, approvals on every applied change, and rollback to a
pre-change baseline when the budget is exhausted.
