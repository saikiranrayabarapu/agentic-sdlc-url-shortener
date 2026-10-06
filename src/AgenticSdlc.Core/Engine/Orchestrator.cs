using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgenticSdlc.Core.Agents;
using AgenticSdlc.Core.Approvals;
using AgenticSdlc.Core.Llm;
using AgenticSdlc.Core.Model;
using AgenticSdlc.Core.Observability;
using AgenticSdlc.Core.Persistence;
using AgenticSdlc.Core.Policy;
using AgenticSdlc.Core.Scenarios;

namespace AgenticSdlc.Core.Engine;

public sealed class OrchestratorOptions
{
    public int MaxParallelism { get; init; } = 3;
    public required string FixturesRoot { get; init; }
    public TimeSpan? MaxRunDuration { get; init; } = TimeSpan.FromMinutes(30);
    /// <summary>Optional console/stream sink for audit events (the CLI prints them live).</summary>
    public Action<AuditEvent>? AuditSink { get; init; }
}

/// <summary>
/// The orchestration engine. Owns every state transition. Agents only propose; this class gates, approves,
/// applies, retries, repairs, rolls back, re-plans, audits and measures.
/// </summary>
public sealed class Orchestrator
{
    private readonly RunStore _store;
    private readonly AgentRegistry _agents;
    private readonly ILlmClient _llm;
    private readonly IApprovalProvider _approvals;
    private readonly PolicyEngine _policy;
    private readonly OrchestratorOptions _options;
    private readonly ConcurrentDictionary<string, bool> _stopRequests = new();
    private readonly object _sync = new();

    public Orchestrator(RunStore store, AgentRegistry agents, ILlmClient llm, IApprovalProvider approvals,
        PolicyEngine policy, OrchestratorOptions options)
    {
        _store = store;
        _agents = agents;
        _llm = llm;
        _approvals = approvals;
        _policy = policy;
        _options = options;
    }

    public RunStore Store => _store;

    // ------------------------------------------------------------------------------------------------
    // Run creation
    // ------------------------------------------------------------------------------------------------
    public RunState CreateRun(ScenarioDefinition scenario, string mode, bool autoApprove, string? injectFailureNodeId = null)
    {
        var runId = RunStore.NewRunId(scenario.Id);
        var workflow = Planner.BuildInitial(scenario);
        var run = new RunState
        {
            RunId = runId,
            Scenario = scenario.Id,
            ScenarioKind = scenario.Kind,
            Mode = mode,
            RequirementText = scenario.Requirement,
            WorkspacePath = _store.WorkspacePath(runId),
            Workflow = workflow,
            AutoApprove = autoApprove,
            InjectFailureNodeId = injectFailureNodeId
        };
        foreach (var n in workflow.Nodes) run.Nodes[n.Id] = new NodeState { NodeId = n.Id };
        _store.Save(run);

        using var audit = OpenAudit(run);
        audit.Append(runId, AuditTypes.RunCreated, Actor.System, $"Run created for scenario '{scenario.Id}' in mode '{mode}'",
            data: new() { ["requirement"] = scenario.Requirement, ["autoApprove"] = autoApprove, ["injectFailure"] = injectFailureNodeId });
        audit.Append(runId, AuditTypes.WorkflowPlanned, Actor.System, $"Dependency graph planned: {workflow.Nodes.Count} nodes in {workflow.Waves().Count} waves",
            data: new()
            {
                ["waves"] = workflow.Waves(),
                ["nodes"] = workflow.Nodes.Select(n => new { n.Id, n.Kind, n.DependsOn, n.Approval, n.MaxRetries }).ToList()
            });

        var workspace = new Workspace.Workspace(run.WorkspacePath, _store.SnapshotsPath(runId));
        if (scenario.SeedFromScenarios.Count > 0)
        {
            var seeded = WorkspaceSeeder.Seed(workspace, _options.FixturesRoot, scenario.SeedFromScenarios);
            audit.Append(runId, AuditTypes.WorkspaceWrite, Actor.System,
                $"Workspace seeded with {seeded.Count} files from recorded outputs of: {string.Join(", ", scenario.SeedFromScenarios)}",
                data: new() { ["files"] = seeded });
        }
        return run;
    }

    public void RequestStop(string runId)
    {
        _stopRequests[runId] = true;
        try { File.WriteAllText(_store.StopSignalPath(runId), DateTimeOffset.UtcNow.ToString("O")); } catch { /* best effort */ }
    }

    // ------------------------------------------------------------------------------------------------
    // Execution
    // ------------------------------------------------------------------------------------------------
    public async Task<RunState> ResumeAsync(string runId, CancellationToken ct)
    {
        var run = _store.Load(runId) ?? throw new ArgumentException($"Run '{runId}' not found.");
        if (run.Status is RunStatus.Completed) return run;
        foreach (var n in run.Nodes.Values)
        {
            if (n.Status is NodeStatus.Running or NodeStatus.AwaitingApproval) n.Status = NodeStatus.Pending;
        }
        run.PendingApprovals.Clear();
        run.PendingClarification = null;
        run.HaltReason = null;
        _stopRequests.TryRemove(runId, out _);
        try { File.Delete(_store.StopSignalPath(runId)); } catch { /* best effort */ }
        using var audit = OpenAudit(run);
        audit.Append(runId, AuditTypes.RunStatusChanged, Actor.Human, "Run resumed from persisted state");
        return await ExecuteAsync(run, ct);
    }

    public async Task<RunState> ExecuteAsync(RunState run, CancellationToken ct)
    {
        using var audit = OpenAudit(run);
        var workspace = new Workspace.Workspace(run.WorkspacePath, _store.SnapshotsPath(run.RunId));
        var started = Stopwatch.StartNew();

        SetRunStatus(run, audit, RunStatus.Running, "Execution started");

        try
        {
            while (true)
            {
                if (StopRequested(run, ct))
                {
                    Halt(run, audit, "Safe-stop requested", "SafeStop");
                    break;
                }
                if (_options.MaxRunDuration is { } max && started.Elapsed > max)
                {
                    Halt(run, audit, $"Maximum run duration {max} exceeded", "SafeStop");
                    break;
                }

                var ready = ReadyNodes(run);
                if (ready.Count == 0)
                {
                    if (run.Nodes.Values.All(n => n.Status == NodeStatus.Succeeded))
                    {
                        Complete(run, audit, started.Elapsed);
                    }
                    else if (run.Status is RunStatus.Running)
                    {
                        // Nothing runnable, nothing succeeded fully: a terminal failure already set the status, or the graph is stuck.
                        var stuck = run.Nodes.Values.Where(n => n.Status != NodeStatus.Succeeded).Select(n => $"{n.NodeId}:{n.Status}");
                        Fail(run, audit, $"No runnable nodes remain: {string.Join(", ", stuck)}");
                    }
                    break;
                }

                foreach (var node in ready)
                {
                    audit.Append(run.RunId, AuditTypes.EntryGate, Actor.System,
                        $"Entry gate passed for '{node.Id}': {node.EntryGate}", node.Id,
                        data: new() { ["dependencies"] = node.DependsOn });
                }

                using var gate = new SemaphoreSlim(_options.MaxParallelism);
                // Task.Run guarantees nodes in the same wave really execute concurrently even when an agent completes synchronously.
                var tasks = ready.Select(node => Task.Run(async () =>
                {
                    await gate.WaitAsync(ct);
                    try { await ExecuteNodeAsync(run, node, workspace, audit, ct); }
                    finally { gate.Release(); }
                }, ct)).ToList();
                await Task.WhenAll(tasks);

                if (run.Status is not RunStatus.Running) break;
            }
        }
        catch (OperationCanceledException)
        {
            Halt(run, audit, "Cancelled by operator (Ctrl+C or token)", "SafeStop");
        }

        return run;
    }

    private List<StageNode> ReadyNodes(RunState run)
    {
        lock (_sync)
        {
            return run.Workflow.Nodes
                .Where(n => run.Nodes[n.Id].Status is NodeStatus.Pending or NodeStatus.Invalidated)
                .Where(n => n.DependsOn.All(d => run.Nodes[d].Status == NodeStatus.Succeeded))
                .ToList();
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Node execution with bounded retries, policy gate, approval gate, apply, repair and re-plan
    // ------------------------------------------------------------------------------------------------
    private async Task ExecuteNodeAsync(RunState run, StageNode node, Workspace.Workspace workspace, AuditLog audit, CancellationToken ct)
    {
        var state = run.Nodes[node.Id];
        var agent = _agents.For(node.Kind);

        while (true)
        {
            if (StopRequested(run, ct)) { Halt(run, audit, "Safe-stop requested", "SafeStop"); return; }

            int attempt;
            string? feedback;
            lock (_sync)
            {
                state.Attempts++;
                attempt = state.Attempts;
                feedback = state.Feedback;
                state.Status = NodeStatus.Running;
                state.StartedAt = DateTimeOffset.UtcNow;
                state.LastError = null;
                run.Metrics.NodesAttempted++;
                _store.Save(run);
            }
            audit.Append(run.RunId, AuditTypes.NodeStarted, Actor.Agent, $"'{node.Id}' attempt {attempt} started ({node.Kind})", node.Id, attempt,
                new() { ["feedback"] = feedback });

            var nodeSw = Stopwatch.StartNew();
            var ctx = new StageContext
            {
                Run = run, Node = node, Attempt = attempt, Workspace = workspace, Llm = _llm, Feedback = feedback,
                Audit = (type, message, data) => audit.Append(run.RunId, type, Actor.Agent, message, node.Id, attempt, data)
            };

            StageResult result;
            if (run.InjectFailureNodeId == node.Id)
            {
                result = StageResult.Fail("Injected failure (--inject-failure) to demonstrate retry, rollback and safe-stop.", "This failure is synthetic.");
            }
            else
            {
                try
                {
                    result = await agent.ExecuteAsync(ctx, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (LlmUnavailableException ex)
                {
                    result = StageResult.Fail($"Model provider unavailable: {ex.Message}", "Model provider unavailable; retrying.");
                }
                catch (Exception ex)
                {
                    result = StageResult.Fail($"Agent raised {ex.GetType().Name}: {ex.Message}", ex.Message);
                }
            }

            lock (_sync)
            {
                run.Metrics.StageLatencyMs[node.Id] = run.Metrics.StageLatencyMs.GetValueOrDefault(node.Id) + nodeSw.Elapsed.TotalMilliseconds;
                if (ctx.Llm is not null && agent is LlmStageAgent) run.Metrics.LlmCalls++;
            }

            switch (result.Outcome)
            {
                case StageOutcome.ClarificationNeeded:
                {
                    var proceed = await HandleClarificationAsync(run, node, state, attempt, result, audit, ct);
                    if (!proceed) return;
                    continue; // re-run the node with answers in context
                }
                case StageOutcome.ChangeRequest:
                {
                    HandleChangeRequest(run, node, state, attempt, result, audit);
                    return; // scheduler will re-run the invalidated upstream node, then this one
                }
                case StageOutcome.Failed:
                {
                    if (await HandleFailureAsync(run, node, state, attempt, result, workspace, audit)) continue;
                    return;
                }
                case StageOutcome.Succeeded:
                {
                    var outcome = await HandleSuccessAsync(run, node, state, attempt, result, workspace, audit, ct);
                    if (outcome == SuccessOutcome.RetryAfterPolicyBlock) continue;
                    return;
                }
            }
        }
    }

    private enum SuccessOutcome { Done, RetryAfterPolicyBlock, Stopped }

    private async Task<SuccessOutcome> HandleSuccessAsync(RunState run, StageNode node, NodeState state, int attempt, StageResult result,
        Workspace.Workspace workspace, AuditLog audit, CancellationToken ct)
    {
        var impact = result.Impact;
        PolicyResult? policy = null;

        // ---- Staleness gate: an upstream stage was invalidated while this node was running (e.g. a change request).
        //      The result was computed from superseded inputs, so discard it and let the scheduler re-run the node.
        string? staleDependency;
        lock (_sync) staleDependency = node.DependsOn.FirstOrDefault(d => run.Nodes[d].Status != NodeStatus.Succeeded);
        if (staleDependency is not null)
        {
            lock (_sync)
            {
                state.Status = NodeStatus.Pending;
                state.Feedback = $"Upstream stage '{staleDependency}' changed while this stage was running; re-run with the new inputs.";
                _store.Save(run);
            }
            audit.Append(run.RunId, AuditTypes.NodeInvalidated, Actor.System,
                $"'{node.Id}' attempt {attempt} result discarded: upstream '{staleDependency}' was invalidated during execution", node.Id, attempt);
            return SuccessOutcome.Done;
        }

        // ---- Policy guardrails (change-control gate) ----
        if (result.ChangeSet is not null)
        {
            policy = _policy.EvaluateChangeSet(result.ChangeSet, workspace, run.ScenarioKind);
            audit.Append(run.RunId, AuditTypes.PolicyCheck, Actor.Policy,
                $"Policy evaluated {policy.RulesEvaluated.Count} rules on {result.ChangeSet.Files.Count} files: {(policy.Blocked ? "BLOCKED" : "pass")}, impact {policy.Impact}",
                node.Id, attempt, new()
                {
                    ["findings"] = policy.Findings.Select(f => new { f.RuleId, Severity = f.Severity.ToString(), f.Path, f.Message }).ToList(),
                    ["impact"] = policy.Impact.ToString()
                });
            if (policy.Blocked)
            {
                var blocked = policy.Findings.Where(f => f.Severity == PolicySeverity.Block).ToList();
                lock (_sync) run.Metrics.PolicyViolations += blocked.Count;
                foreach (var f in blocked)
                    audit.Append(run.RunId, AuditTypes.PolicyViolation, Actor.Policy, $"{f.RuleId} {f.Path}: {f.Message}", node.Id, attempt);
                AddDecision(run, Actor.Policy, "PolicyEngine", node.Id, "PolicyBlock",
                    $"Change set blocked by {string.Join(", ", blocked.Select(b => b.RuleId).Distinct())}; attempt will be retried with findings as feedback.");
                var failure = StageResult.Fail("Policy guardrails blocked the change set.",
                    "Policy violations must be fixed:\n" + string.Join('\n', blocked.Select(b => $"- [{b.RuleId}] {b.Path}: {b.Message}")));
                return await HandleFailureAsync(run, node, state, attempt, failure, workspace, audit)
                    ? SuccessOutcome.RetryAfterPolicyBlock
                    : SuccessOutcome.Stopped;
            }
            if (policy.Impact > impact) impact = policy.Impact;
        }
        lock (_sync) state.Impact = impact;

        // ---- Human approval gate ----
        var needsApproval = node.Approval == ApprovalRule.Always || (node.Approval == ApprovalRule.IfHighImpact && impact == ImpactLevel.High);
        if (needsApproval)
        {
            var approved = await RequestApprovalAsync(run, node, state, attempt, result, impact, policy, audit, ct);
            if (!approved) return SuccessOutcome.Stopped;
        }

        // ---- Apply side effects behind a snapshot (reversible) ----
        if (result.ChangeSet is not null)
        {
            var snapshotId = workspace.Snapshot($"{node.Id}-a{attempt}");
            lock (_sync) run.SnapshotIds.Add(snapshotId);
            audit.Append(run.RunId, AuditTypes.Snapshot, Actor.System, $"Workspace snapshot '{snapshotId}' taken before applying '{node.Id}'", node.Id, attempt);

            var touched = workspace.Apply(result.ChangeSet);
            audit.Append(run.RunId, AuditTypes.WorkspaceWrite, Actor.System, $"Applied {touched.Count} file change(s) from '{node.Id}'", node.Id, attempt,
                new() { ["files"] = touched });
        }

        // ---- Record artifact + lineage, close recovery windows, mark succeeded ----
        var upstreamIds = node.DependsOn.Select(d => run.LatestArtifact(d)?.Id).Where(id => id is not null).Select(id => id!).ToList();
        var artifact = new Artifact
        {
            Id = $"{node.Id}-a{attempt}",
            NodeId = node.Id,
            Attempt = attempt,
            Type = result.ArtifactType,
            Summary = result.Summary,
            Content = result.Content ?? JsonSerializer.SerializeToElement(new { summary = result.Summary }),
            InputsHash = ComputeInputsHash(run, node, state.Feedback)
        };
        lock (_sync)
        {
            foreach (var old in run.Artifacts.Where(a => a.NodeId == node.Id)) old.Superseded = true;
            run.Artifacts.Add(artifact);
            state.ArtifactIds.Add(artifact.Id);
            state.Status = NodeStatus.Succeeded;
            state.CompletedAt = DateTimeOffset.UtcNow;
            state.Feedback = null;
            run.Metrics.NodesSucceeded++;
            foreach (var w in run.Metrics.Recoveries.Where(w => w.NodeId == node.Id && w.RecoveredAt is null)) w.RecoveredAt = DateTimeOffset.UtcNow;
            if (node.Kind == StageKind.Verification)
                foreach (var w in run.Metrics.Recoveries.Where(w => w.Kind == "Repair" && w.RecoveredAt is null)) w.RecoveredAt = DateTimeOffset.UtcNow;
            _store.Save(run);
        }
        audit.Append(run.RunId, AuditTypes.ExitGate, Actor.System, $"Exit gate passed for '{node.Id}': {node.ExitGate}", node.Id, attempt);
        audit.Append(run.RunId, AuditTypes.NodeSucceeded, Actor.Agent, $"'{node.Id}' succeeded: {Truncate(result.Summary, 200)}", node.Id, attempt,
            new() { ["artifactId"] = artifact.Id, ["artifactType"] = artifact.Type, ["inputsHash"] = artifact.InputsHash, ["basedOn"] = upstreamIds });
        return SuccessOutcome.Done;
    }

    private async Task<bool> RequestApprovalAsync(RunState run, StageNode node, NodeState state, int attempt, StageResult result,
        ImpactLevel impact, PolicyResult? policy, AuditLog audit, CancellationToken ct)
    {
        var summary = new StringBuilder();
        summary.AppendLine(result.Summary);
        if (result.ChangeSet is not null)
        {
            summary.AppendLine($"Files ({result.ChangeSet.Files.Count}):");
            foreach (var f in result.ChangeSet.Files.Take(25)) summary.AppendLine($"  {f.Action,-6} {f.Path}");
            if (result.ChangeSet.Risks.Count > 0) summary.AppendLine("Risks: " + string.Join("; ", result.ChangeSet.Risks));
        }
        if (policy is not null && policy.Findings.Count > 0)
            summary.AppendLine("Policy notes: " + string.Join("; ", policy.Findings.Select(f => $"[{f.RuleId}] {f.Message}")));
        if (node.Kind == StageKind.ReleaseReadiness && result.Content is { } c && c.TryGetProperty("recommendation", out var rec))
            summary.AppendLine($"Release recommendation: {rec}");

        var pending = new PendingApproval
        {
            Id = $"{node.Id}-a{attempt}",
            NodeId = node.Id,
            Title = node.Approval == ApprovalRule.Always ? $"Approve stage '{node.Name}' (approval always required)" : $"Approve HIGH-impact change from '{node.Name}'",
            Summary = summary.ToString().TrimEnd(),
            Impact = impact,
            ArtifactIds = node.DependsOn.Select(d => run.LatestArtifact(d)?.Id).Where(x => x is not null).Select(x => x!).ToList()
        };

        lock (_sync)
        {
            run.PendingApprovals.Add(pending);
            run.Status = RunStatus.AwaitingApproval;
            state.Status = NodeStatus.AwaitingApproval;
            run.Metrics.ApprovalsRequested++;
            _store.Save(run);
        }
        audit.Append(run.RunId, AuditTypes.ApprovalRequested, Actor.System, $"Human approval requested for '{node.Id}' (impact {impact})", node.Id, attempt,
            new() { ["approvalId"] = pending.Id, ["summary"] = pending.Summary });

        ApprovalDecision decision;
        try
        {
            decision = await _approvals.RequestApprovalAsync(run, pending, ct);
        }
        catch (TimeoutException ex)
        {
            lock (_sync) { run.PendingApprovals.Remove(pending); state.Status = NodeStatus.Pending; }
            Halt(run, audit, ex.Message, "SafeStop");
            return false;
        }

        lock (_sync)
        {
            run.PendingApprovals.Remove(pending);
            run.Status = RunStatus.Running;
        }
        var actor = decision.Automated ? Actor.System : Actor.Human;
        audit.Append(run.RunId, AuditTypes.ApprovalDecided, actor, $"'{node.Id}' {(decision.Approved ? "APPROVED" : "REJECTED")} by {decision.Approver}", node.Id, attempt,
            new() { ["approvalId"] = pending.Id, ["comment"] = decision.Comment, ["automated"] = decision.Automated }, decision.Approver);
        AddDecision(run, actor, decision.Approver, node.Id, decision.Approved ? "Approve" : "Reject",
            decision.Comment ?? (decision.Automated ? "Automated approval (--auto-approve)" : "Approved by human reviewer"), pending.ArtifactIds);

        if (!decision.Approved)
        {
            // A rejection is not a stage failure: the proposal goes back to Pending with the reviewer's comment as feedback,
            // so `resume` re-runs the stage and the agent revises its proposal before approval is requested again.
            lock (_sync)
            {
                state.Status = NodeStatus.Pending;
                state.LastError = $"Rejected by {decision.Approver}";
                state.Feedback = $"The reviewer ({decision.Approver}) rejected the previous proposal{(decision.Comment is null ? "." : $": {decision.Comment}")} Revise it before resubmitting.";
            }
            Halt(run, audit, $"Change rejected by {decision.Approver}{(decision.Comment is null ? "" : $": {decision.Comment}")}", "Reject");
            return false;
        }
        lock (_sync) { run.Metrics.ApprovalsGranted++; state.ApprovalGranted = true; }
        return true;
    }

    /// <summary>Returns true when the node should be retried in-place; false when the run has been stopped or the node handed off.</summary>
    private async Task<bool> HandleFailureAsync(RunState run, StageNode node, NodeState state, int attempt, StageResult result,
        Workspace.Workspace workspace, AuditLog audit)
    {
        lock (_sync)
        {
            state.LastError = result.Error;
            state.Feedback = result.Feedback;
            run.Metrics.NodesFailed++;
            run.Metrics.Recoveries.Add(new RecoveryWindow { NodeId = node.Id, FailedAt = DateTimeOffset.UtcNow, Kind = node.Kind == StageKind.Verification ? "Repair" : "Retry" });
            _store.Save(run);
        }
        audit.Append(run.RunId, AuditTypes.NodeFailed, Actor.Agent, $"'{node.Id}' attempt {attempt} failed: {Truncate(result.Error ?? "", 300)}", node.Id, attempt,
            new() { ["feedback"] = result.Feedback, ["artifact"] = result.Content });

        // Verification failures are repaired upstream: re-run Implementation with the failure as feedback (non-linear path).
        if (node.Kind == StageKind.Verification && run.Workflow.Has("implementation"))
        {
            if (run.RepairCyclesUsed < run.Workflow.MaxRepairCycles)
            {
                lock (_sync)
                {
                    run.RepairCyclesUsed++;
                    run.Metrics.RepairCycles++;
                    Invalidate(run, "implementation", result.Feedback, keepPendingDependents: true);
                    state.Status = NodeStatus.Pending;
                    _store.Save(run);
                }
                audit.Append(run.RunId, AuditTypes.RepairCycle, Actor.System,
                    $"Verification failed; repair cycle {run.RepairCyclesUsed}/{run.Workflow.MaxRepairCycles}: re-running 'implementation' with failure feedback", node.Id, attempt);
                AddDecision(run, Actor.System, "Orchestrator", node.Id, "Repair",
                    $"Exit gate failed ({Truncate(result.Error ?? "", 120)}). Bounded repair cycle {run.RepairCyclesUsed}/{run.Workflow.MaxRepairCycles} re-runs implementation with the evidence as feedback.");
                return false;
            }
            audit.Append(run.RunId, AuditTypes.Warning, Actor.System, "Repair budget exhausted", node.Id, attempt);
            RollbackAndStop(run, node, state, workspace, audit, $"Verification still failing after {run.Workflow.MaxRepairCycles} repair cycles");
            return false;
        }

        if (attempt <= node.MaxRetries)
        {
            lock (_sync) run.Metrics.Retries++;
            audit.Append(run.RunId, AuditTypes.Retry, Actor.System, $"Retrying '{node.Id}' ({attempt}/{node.MaxRetries} retries used) with feedback", node.Id, attempt);
            AddDecision(run, Actor.System, "Orchestrator", node.Id, "Retry", $"Attempt {attempt} failed: {Truncate(result.Error ?? "", 120)}. Retry {attempt}/{node.MaxRetries} with feedback.");
            return true;
        }

        RollbackAndStop(run, node, state, workspace, audit, $"'{node.Id}' exhausted {node.MaxRetries} retries: {result.Error}");
        return false;
    }

    private void RollbackAndStop(RunState run, StageNode node, NodeState state, Workspace.Workspace workspace, AuditLog audit, string reason)
    {
        string? restored = null;
        lock (_sync)
        {
            state.Status = NodeStatus.Failed;
            if (run.SnapshotIds.Count > 0)
            {
                restored = run.SnapshotIds[0]; // the pre-change baseline of this run
                try
                {
                    workspace.Restore(restored);
                    run.Metrics.Rollbacks++;
                }
                catch (Exception ex)
                {
                    run.Warnings.Add($"Rollback to {restored} failed: {ex.Message}");
                    restored = null;
                }
            }
            run.Status = restored is null ? RunStatus.Failed : RunStatus.RolledBack;
            run.HaltReason = reason;
            run.CompletedAt = DateTimeOffset.UtcNow;
            run.Metrics.EndToEndLatencyMs = (run.CompletedAt.Value - run.CreatedAt).TotalMilliseconds;
            _store.Save(run);
        }
        if (restored is not null)
        {
            audit.Append(run.RunId, AuditTypes.Rollback, Actor.System, $"Workspace rolled back to snapshot '{restored}'", node.Id, data: new() { ["snapshot"] = restored });
            AddDecision(run, Actor.System, "Orchestrator", node.Id, "Rollback", $"{reason}. Workspace restored to the pre-change baseline so no partial change remains.");
        }
        audit.Append(run.RunId, AuditTypes.RunStatusChanged, Actor.System, $"Run {run.Status}: {reason}", node.Id);
    }

    private void HandleChangeRequest(RunState run, StageNode node, NodeState state, int attempt, StageResult result, AuditLog audit)
    {
        var target = result.ChangeRequestTargetNodeId ?? "design";
        var upstream = TransitiveDependencies(run.Workflow, node.Id);
        if (!upstream.Contains(target) || run.ReplansUsed >= run.Workflow.MaxReplans)
        {
            var why = !upstream.Contains(target) ? $"'{target}' is not upstream of '{node.Id}'" : $"re-plan budget {run.Workflow.MaxReplans} exhausted";
            audit.Append(run.RunId, AuditTypes.Warning, Actor.System, $"Change request rejected: {why}", node.Id, attempt);
            lock (_sync)
            {
                state.Status = NodeStatus.Failed;
                state.LastError = $"Change request rejected: {why}";
                run.Status = RunStatus.Failed;
                run.HaltReason = state.LastError;
                _store.Save(run);
            }
            return;
        }

        lock (_sync)
        {
            run.ReplansUsed++;
            run.Metrics.Replans++;
            var crArtifact = new Artifact
            {
                Id = $"{node.Id}-a{attempt}-changerequest", NodeId = node.Id, Attempt = attempt, Type = "ChangeRequest",
                Summary = result.Summary, Content = result.Content ?? JsonSerializer.SerializeToElement(new { reason = result.ChangeRequestReason }),
                InputsHash = ComputeInputsHash(run, node, state.Feedback)
            };
            run.Artifacts.Add(crArtifact);
            Invalidate(run, target, $"Change requested by '{node.Id}': {result.ChangeRequestReason}", keepPendingDependents: true);
            state.Status = NodeStatus.Pending;
            _store.Save(run);
        }
        audit.Append(run.RunId, AuditTypes.WorkflowReplanned, Actor.Agent,
            $"'{node.Id}' requested a change to '{target}': {Truncate(result.ChangeRequestReason ?? "", 200)}. '{target}' and its dependents invalidated (re-plan {run.ReplansUsed}/{run.Workflow.MaxReplans}).",
            node.Id, attempt, new() { ["target"] = target, ["reason"] = result.ChangeRequestReason });
        AddDecision(run, Actor.Agent, node.Id, target, "Replan",
            $"Upstream output changed: {result.ChangeRequestReason}. Downstream artifacts superseded; '{target}' will re-run with this feedback.");
    }

    private async Task<bool> HandleClarificationAsync(RunState run, StageNode node, NodeState state, int attempt, StageResult result, AuditLog audit, CancellationToken ct)
    {
        var pending = new PendingClarification { Id = $"{node.Id}-a{attempt}-clarify", NodeId = node.Id, Questions = result.Questions };
        lock (_sync)
        {
            run.PendingClarification = pending;
            run.Status = RunStatus.AwaitingClarification;
            state.Status = NodeStatus.AwaitingApproval;
            if (result.Content is { } draft)
            {
                run.Artifacts.Add(new Artifact
                {
                    Id = $"{node.Id}-a{attempt}-draft", NodeId = node.Id, Attempt = attempt, Type = "RequirementDraft",
                    Summary = result.Summary, Content = draft, InputsHash = ComputeInputsHash(run, node, state.Feedback), Superseded = true
                });
            }
            _store.Save(run);
        }
        audit.Append(run.RunId, AuditTypes.ClarificationRequested, Actor.Agent,
            $"'{node.Id}' needs {result.Questions.Count} clarification(s) before planning can continue", node.Id, attempt,
            new() { ["questions"] = result.Questions.Select(q => new { q.Id, q.Question, q.Options, q.DefaultAssumption, Impact = q.Impact.ToString() }).ToList() });

        ClarificationAnswers answers;
        try
        {
            answers = await _approvals.RequestClarificationAsync(run, pending, ct);
        }
        catch (TimeoutException ex)
        {
            lock (_sync) { run.PendingClarification = null; state.Status = NodeStatus.Pending; }
            Halt(run, audit, ex.Message, "SafeStop");
            return false;
        }

        var actor = answers.Automated ? Actor.System : Actor.Human;
        lock (_sync)
        {
            foreach (var kv in answers.Answers) run.ClarificationAnswers[kv.Key] = kv.Value;
            run.PendingClarification = null;
            run.Status = RunStatus.Running;
            state.Status = NodeStatus.Pending;
            state.Feedback = "Ambiguities were resolved by the reviewer; produce the final specification using the decided answers.";
        }
        audit.Append(run.RunId, AuditTypes.ClarificationAnswered, actor, $"Clarifications answered by {answers.Approver}", node.Id, attempt,
            new() { ["answers"] = answers.Answers, ["automated"] = answers.Automated }, answers.Approver);
        AddDecision(run, actor, answers.Approver, node.Id, "Clarify",
            string.Join(" | ", answers.Answers.Select(kv => $"{kv.Key} = {kv.Value}")));

        var changes = Planner.ReplanAfterClarification(run.Workflow, answers.Answers);
        if (changes.Count > 0)
        {
            lock (_sync)
            {
                foreach (var n in run.Workflow.Nodes.Where(n => !run.Nodes.ContainsKey(n.Id))) run.Nodes[n.Id] = new NodeState { NodeId = n.Id };
                run.ReplansUsed++;
                run.Metrics.Replans++;
                _store.Save(run);
            }
            audit.Append(run.RunId, AuditTypes.WorkflowReplanned, Actor.System, $"Workflow re-planned after clarification ({changes.Count} change(s))", node.Id, attempt,
                new() { ["changes"] = changes, ["waves"] = run.Workflow.Waves() });
            AddDecision(run, Actor.System, "Planner", node.Id, "Replan", string.Join("; ", changes));
        }
        else
        {
            lock (_sync) _store.Save(run);
        }
        return true;
    }

    // ------------------------------------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------------------------------------
    private void Invalidate(RunState run, string nodeId, string? feedback, bool keepPendingDependents)
    {
        var target = run.Nodes[nodeId];
        target.Status = NodeStatus.Invalidated;
        target.Feedback = feedback;
        foreach (var a in run.Artifacts.Where(a => a.NodeId == nodeId)) a.Superseded = true;
        foreach (var depId in run.Workflow.TransitiveDependents(nodeId))
        {
            var dep = run.Nodes[depId];
            if (keepPendingDependents && dep.Status == NodeStatus.Pending) continue;
            if (dep.Status is NodeStatus.Succeeded or NodeStatus.Failed or NodeStatus.Invalidated)
            {
                dep.Status = NodeStatus.Pending;
                foreach (var a in run.Artifacts.Where(a => a.NodeId == depId)) a.Superseded = true;
            }
        }
    }

    private static HashSet<string> TransitiveDependencies(WorkflowDefinition wf, string nodeId)
    {
        var result = new HashSet<string>();
        var queue = new Queue<string>(wf.Node(nodeId).DependsOn);
        while (queue.Count > 0)
        {
            var next = queue.Dequeue();
            if (!result.Add(next)) continue;
            foreach (var d in wf.Node(next).DependsOn) queue.Enqueue(d);
        }
        return result;
    }

    private void AddDecision(RunState run, Actor actor, string actorName, string nodeId, string kind, string rationale, List<string>? basedOn = null)
    {
        lock (_sync)
        {
            run.Decisions.Add(new Decision
            {
                Id = $"D{run.Decisions.Count + 1:000}", Actor = actor, ActorName = actorName, NodeId = nodeId, Kind = kind, Rationale = rationale,
                BasedOnArtifactIds = basedOn ?? run.Workflow.Node(nodeId).DependsOn.Select(d => run.LatestArtifact(d)?.Id).Where(x => x is not null).Select(x => x!).ToList()
            });
            _store.Save(run);
        }
    }

    private void SetRunStatus(RunState run, AuditLog audit, RunStatus status, string message)
    {
        lock (_sync) { run.Status = status; _store.Save(run); }
        audit.Append(run.RunId, AuditTypes.RunStatusChanged, Actor.System, $"{status}: {message}");
    }

    private void Halt(RunState run, AuditLog audit, string reason, string decisionKind)
    {
        lock (_sync)
        {
            if (run.Status is RunStatus.Halted or RunStatus.RolledBack or RunStatus.Failed or RunStatus.Completed) return;
            run.Status = RunStatus.Halted;
            run.HaltReason = reason;
            foreach (var n in run.Nodes.Values.Where(n => n.Status is NodeStatus.Running or NodeStatus.AwaitingApproval)) n.Status = NodeStatus.Pending;
            _store.Save(run);
        }
        audit.Append(run.RunId, AuditTypes.SafeStop, Actor.System, $"Run halted safely: {reason}. State persisted; resume with 'resume {run.RunId}'.");
        AddDecision(run, decisionKind == "Reject" ? Actor.Human : Actor.System, "Orchestrator", run.Workflow.Nodes[0].Id, decisionKind, reason);
    }

    private void Fail(RunState run, AuditLog audit, string reason)
    {
        lock (_sync)
        {
            run.Status = RunStatus.Failed;
            run.HaltReason = reason;
            run.CompletedAt = DateTimeOffset.UtcNow;
            run.Metrics.EndToEndLatencyMs = (run.CompletedAt.Value - run.CreatedAt).TotalMilliseconds;
            _store.Save(run);
        }
        audit.Append(run.RunId, AuditTypes.RunStatusChanged, Actor.System, $"Failed: {reason}");
    }

    private void Complete(RunState run, AuditLog audit, TimeSpan elapsed)
    {
        lock (_sync)
        {
            run.Status = RunStatus.Completed;
            run.CompletedAt = DateTimeOffset.UtcNow;
            run.Metrics.EndToEndLatencyMs = (run.CompletedAt.Value - run.CreatedAt).TotalMilliseconds;
            _store.Save(run);
        }
        var m = run.Metrics;
        audit.Append(run.RunId, AuditTypes.Metrics, Actor.System,
            $"Run completed in {elapsed.TotalSeconds:F1}s. success rate {m.SuccessRate:P0}, retries {m.Retries}, repairs {m.RepairCycles}, rollbacks {m.Rollbacks}, replans {m.Replans}, approvals {m.ApprovalsGranted}/{m.ApprovalsRequested}, MTTR {(m.MttrMs is null ? "n/a" : $"{m.MttrMs / 1000.0:F1}s")}",
            data: new()
            {
                ["successRate"] = m.SuccessRate, ["retries"] = m.Retries, ["repairCycles"] = m.RepairCycles, ["rollbacks"] = m.Rollbacks,
                ["replans"] = m.Replans, ["policyViolations"] = m.PolicyViolations, ["mttrMs"] = m.MttrMs, ["endToEndMs"] = m.EndToEndLatencyMs,
                ["stageLatencyMs"] = m.StageLatencyMs
            });
        audit.Append(run.RunId, AuditTypes.RunStatusChanged, Actor.System, "Completed: all exit gates passed and release approved");
    }

    private bool StopRequested(RunState run, CancellationToken ct)
        => ct.IsCancellationRequested || _stopRequests.ContainsKey(run.RunId) || File.Exists(_store.StopSignalPath(run.RunId));

    private AuditLog OpenAudit(RunState run)
    {
        var audit = new AuditLog(_store.AuditPath(run.RunId));
        if (_options.AuditSink is not null) audit.AddSink(_options.AuditSink);
        return audit;
    }

    private static string ComputeInputsHash(RunState run, StageNode node, string? feedback)
    {
        var sb = new StringBuilder(run.RequirementText);
        foreach (var d in node.DependsOn)
        {
            var a = run.LatestArtifact(d);
            sb.Append('|').Append(d).Append(':').Append(a?.Id).Append(':').Append(a?.InputsHash);
        }
        foreach (var kv in run.ClarificationAnswers.OrderBy(k => k.Key)) sb.Append('|').Append(kv.Key).Append('=').Append(kv.Value);
        sb.Append('|').Append(feedback);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16].ToLowerInvariant();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}
