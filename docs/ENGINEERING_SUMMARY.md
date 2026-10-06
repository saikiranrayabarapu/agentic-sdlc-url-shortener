# Final engineering summary

## Plan and rationale

The assignment asks for two things that are easy to conflate: an **agentic orchestration layer** that drives a change
through the SDLC under governance, and a **URL shortener** as the workload that proves the layer produces real
engineering output. The plan was therefore layered:

1. Build the orchestration engine first as plain, testable C#: an explicit dependency graph, a scheduler with parallel
   waves, entry/exit gates, bounded retries, a cross-stage repair loop, change requests, clarification-driven
   re-planning, a policy engine, human approval providers, snapshots/rollback, safe-stop/resume, a hash-chained audit
   log and reliability metrics.
2. Build the URL shortener to production standards independently (49 tests), then **derive the three scenario
   recordings from it**: version 1.0 (greenfield), the analytics delta (brownfield, including a deliberately broken
   first attempt), and the reliability delta (ambiguous, including an infeasible first design).
3. Make the default mode reproducible without any API key (replay), while keeping live model modes available through
   the same agent code so the design is honest about what an agent does at runtime.
4. Put a thin host on top (CLI + HTTP API) and an Angular dashboard for the human-in-the-loop surfaces.
5. Prove everything end to end: unit tests for the engine, the product's own tests, and headless runs of all three
   scenarios plus an injected-failure run that must end in rollback (all reproducible from a fresh clone).

Why this shape: the "critical differentiator" in the brief is governance that is **visible and testable**. A general
workflow engine would have hidden exactly the parts the exercise evaluates; a linear chain would not demonstrate
re-planning or repair. A small bespoke scheduler with the graph as data makes every transition auditable and lets
unit tests pin each governance path.

## Artifacts

| Artifact | Where |
|---|---|
| Orchestration engine, agents, policy, approvals, audit, metrics | `src/AgenticSdlc.Core` |
| CLI + HTTP API host | `src/AgenticSdlc.Host` |
| Angular dashboard | `dashboard/` |
| URL shortener service (final, v1.2) | `src/UrlShortener.Api` |
| Shortener tests | `tests/UrlShortener.Api.Tests` |
| Engine tests | `tests/AgenticSdlc.Core.Tests` |
| Recorded agent outputs per scenario (requirement specs, design docs with ADRs, change sets, release reports) | `fixtures/replay/{greenfield,brownfield,ambiguous}` |
| Architecture overview | `ARCHITECTURE.md` |
| Scenario write-ups (decomposition, orchestration trace, validation) | `docs/scenarios/` |
| Testing approach | `docs/TESTING.md` |
| One-command wrappers (test, scenario, serve, shortener) | `run.ps1`, `run.sh` |
| Per-run evidence (generated at run time) | `runs/<id>/state.json`, `audit.jsonl`, `workspace/`, `snapshots/` |

## Risks, trade-offs and validation

| Risk / trade-off | Position taken | Validation |
|---|---|---|
| Model output is unreliable (hallucinated success, malformed JSON) | Agents only *propose*; exit gates are deterministic where it matters (real build + tests); JSON contracts are validated and violations become feedback | brownfield run: compiler error caught and repaired; engine tests for contract failures |
| Unreviewed changes reaching code | Policy gate + approval gate sit between proposal and apply; approvals audited with actor; rejection halts | engine tests `Rejected_approval_halts...`, `Policy_blocks_secrets...`; console + API manual runs |
| Runaway loops / cost | Bounded per-node retries, repair budget, re-plan budget, max run duration, safe-stop | `Repair_budget_is_bounded...`, injected-failure CI job |
| Partial changes after failure | Snapshot before every apply; rollback to the pre-change baseline on exhaustion | `Exhausted_retries_roll_back...`; injected-failure run restores the seeded baseline |
| Ambiguous requirements silently "resolved" by the model | High-impact ambiguities force a human clarification checkpoint; answers are recorded and hashed into downstream artifacts; approval tightens afterwards | ambiguous scenario; `Clarification_pauses_for_a_human...` |
| Upstream changes invalidating in-flight work | Change requests invalidate dependents; staleness gate discards results computed from superseded inputs | `Change_request_invalidates...`; ambiguous run trace |
| Audit log tampering | SHA-256 hash chain, `--verify` | `Hash_chain_verifies_and_detects_tampering` |
| Replay mode could be mistaken for live autonomy | Documented plainly; same agent code runs live with `--mode anthropic` / `--mode claude-cli`; `--record` writes new fixtures; fallback to replay is audited as `Fallback` | manual connectivity check of the CLI mode; `Fallback_client_uses_replay...` |
| SQLite as the product store | Chosen for zero-step local runs; EF Core keeps PostgreSQL cheap; limitations documented in the product README and release reports | product tests; release risk registers |
| Fixed-window rate limiting is per instance | Accepted within the clarified single-instance scope; documented as a limitation | `RateLimitTests`; release report Warn item |

## Assumptions

- Reviewers run on a machine with the .NET 10 SDK and (for the dashboard) Node 24; Docker is not assumed.
- Replay mode is an acceptable default for a reproducible demo; live mode requires an API key or a Claude Code login.
- A single human reviewer role is sufficient; there is no separation between approver identities beyond the recorded name.
- The dependency allow-list (Microsoft.*, System.*, xunit, coverlet, a few well-known libraries) represents the
  organisation's approved set for this exercise.
- "Production-grade" means clone-and-run quality, tests, documentation and operational hygiene, not a hosted deployment.

## Limitations

- The recorded agent outputs were produced with an AI assistant during development and curated so that the demo is
  deterministic; a live run may produce different (and possibly failing) proposals, which is precisely what the gates
  and repair loop are for.
- Live modes are not covered by automated tests.
- Rollback restores a directory snapshot; it does not undo external side effects (none exist in this prototype).
- The policy engine is pattern-based; it is a change-control guardrail, not a substitute for SAST.
- The dashboard polls rather than streams, and has no authentication; it is a local operator UI.
- Approval decisions are not cryptographically signed; the audit chain proves integrity of the log, not the identity
  of the approver.
- Metrics are computed from run state files; there is no time-series store.

## Controlled autonomy, in one paragraph

Agents execute multi-step work inside explicit boundaries: they may read the workspace and upstream artifacts, propose
change sets and raise change requests or clarification questions. They may not write to the workspace, bypass a gate,
exceed a retry/repair/re-plan budget, add an unapproved dependency, or touch protected paths. Humans decide on
ambiguity, approve high-impact and release changes, can stop the run at any time, and own the final quality judgement
with a complete, tamper-evident record of how the outcome was produced.
