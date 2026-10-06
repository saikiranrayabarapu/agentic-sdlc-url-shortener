using AgenticSdlc.Core.Agents;
using AgenticSdlc.Core.Approvals;
using AgenticSdlc.Core.Model;
using AgenticSdlc.Core.Observability;
using AgenticSdlc.Core.Scenarios;

namespace AgenticSdlc.Core.Tests;

public sealed class OrchestratorTests
{
    [Fact]
    public async Task Happy_path_completes_all_nodes_and_leaves_a_verifiable_audit_chain()
    {
        using var h = new Harness();
        var run = await h.RunAsync();

        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.All(run.Nodes.Values, n => Assert.Equal(NodeStatus.Succeeded, n.Status));
        Assert.Equal(1.0, run.Metrics.SuccessRate);
        Assert.Equal(1, run.Metrics.ApprovalsRequested); // release always requires approval
        Assert.NotNull(run.Metrics.EndToEndLatencyMs);
        Assert.Equal(run.Workflow.Nodes.Count, run.Artifacts.Count(a => !a.Superseded));

        var (ok, broken) = AuditLog.Verify(h.Store.AuditPath(run.RunId));
        Assert.True(ok, $"audit chain broken at {broken}");
    }

    [Fact]
    public async Task Nodes_in_the_same_wave_run_concurrently()
    {
        using var h = new Harness();
        var gate = new TaskCompletionSource();
        var implStarted = new TaskCompletionSource();
        var testsStarted = new TaskCompletionSource();
        h.Agents[StageKind.Implementation] = new BlockingAgent(StageKind.Implementation, implStarted, gate.Task);
        h.Agents[StageKind.TestAuthoring] = new BlockingAgent(StageKind.TestAuthoring, testsStarted, gate.Task);

        var runTask = h.RunAsync();
        var bothStarted = Task.WhenAll(implStarted.Task, testsStarted.Task);
        var winner = await Task.WhenAny(bothStarted, Task.Delay(5000));
        gate.SetResult();
        var run = await runTask;

        Assert.Same(bothStarted, winner); // both were running at the same time before either finished
        Assert.Equal(RunStatus.Completed, run.Status);
    }

    [Fact]
    public async Task Failed_attempt_is_retried_with_feedback_and_recovery_is_measured()
    {
        using var h = new Harness();
        string? feedbackSeen = null;
        h.Script(StageKind.Requirements, ctx =>
        {
            if (ctx.Attempt == 1) return StageResult.Fail("spec incomplete", "add acceptance criteria");
            feedbackSeen = ctx.Feedback;
            return ScriptedAgent.Ok("spec");
        });

        var run = await h.RunAsync();

        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Equal(2, run.Nodes["requirements"].Attempts);
        Assert.Equal(1, run.Metrics.Retries);
        Assert.Equal("add acceptance criteria", feedbackSeen);
        Assert.NotNull(run.Metrics.MttrMs);
        Assert.Contains(run.Decisions, d => d.Kind == "Retry" && d.NodeId == "requirements");
    }

    [Fact]
    public async Task Exhausted_retries_roll_back_the_workspace_and_stop_the_run()
    {
        using var h = new Harness();
        h.Script(StageKind.Design, _ => ScriptedAgent.WithChanges("design notes", ("DESIGN.md", "# design")));
        h.Script(StageKind.Implementation, _ => StageResult.Fail("cannot implement"));

        var run = await h.RunAsync();

        Assert.Equal(RunStatus.RolledBack, run.Status);
        Assert.Equal(NodeStatus.Failed, run.Nodes["implementation"].Status);
        Assert.Equal(3, run.Nodes["implementation"].Attempts); // 1 + MaxRetries(2)
        Assert.Equal(1, run.Metrics.Rollbacks);
        Assert.False(File.Exists(Path.Combine(run.WorkspacePath, "DESIGN.md")), "rollback should restore the pre-change baseline");
        Assert.Contains(run.Decisions, d => d.Kind == "Rollback");
        Assert.Contains("exhausted", run.HaltReason);
    }

    [Fact]
    public async Task Verification_failure_triggers_a_bounded_repair_cycle_on_implementation()
    {
        using var h = new Harness();
        var verifications = 0;
        h.Script(StageKind.Implementation, ctx => ScriptedAgent.WithChanges($"impl v{ctx.Attempt}", ("src/A.cs", $"// attempt {ctx.Attempt}")));
        h.Script(StageKind.Verification, _ => ++verifications == 1
            ? StageResult.Fail("Tests failed", "1 test failed: Expected 200 got 500")
            : ScriptedAgent.Ok("green"));

        var run = await h.RunAsync();

        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Equal(1, run.Metrics.RepairCycles);
        Assert.Equal(2, run.Nodes["implementation"].Attempts);
        Assert.Equal(2, run.Nodes["verification"].Attempts);
        Assert.Equal("// attempt 2", File.ReadAllText(Path.Combine(run.WorkspacePath, "src/A.cs")));
        Assert.Contains(run.Decisions, d => d.Kind == "Repair");
        Assert.True(run.Artifacts.Where(a => a.NodeId == "implementation").Count(a => a.Superseded) == 1);
    }

    [Fact]
    public async Task Repair_budget_is_bounded_and_ends_in_rollback()
    {
        using var h = new Harness();
        h.Script(StageKind.Implementation, _ => ScriptedAgent.WithChanges("impl", ("src/A.cs", "x")));
        h.Script(StageKind.Verification, _ => StageResult.Fail("still failing"));

        var run = await h.RunAsync();

        Assert.Equal(RunStatus.RolledBack, run.Status);
        Assert.Equal(run.Workflow.MaxRepairCycles, run.Metrics.RepairCycles);
        Assert.False(File.Exists(Path.Combine(run.WorkspacePath, "src/A.cs")));
    }

    [Fact]
    public async Task Change_request_invalidates_upstream_design_and_replans()
    {
        using var h = new Harness();
        h.Script(StageKind.Implementation, ctx => ctx.Attempt == 1
            ? new StageResult { Outcome = StageOutcome.ChangeRequest, Summary = "design infeasible", ChangeRequestTargetNodeId = "design", ChangeRequestReason = "needs unapproved dependency" }
            : ScriptedAgent.Ok("implemented against revised design"));
        var designFeedback = new Dictionary<int, string?>();
        h.Script(StageKind.Design, ctx => { designFeedback[ctx.Attempt] = ctx.Feedback; return ScriptedAgent.Ok($"design v{ctx.Attempt}"); });

        var run = await h.RunAsync();

        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Equal(2, run.Nodes["design"].Attempts);
        Assert.Equal(2, run.Nodes["implementation"].Attempts);
        Assert.Equal(1, run.Metrics.Replans);
        Assert.Contains(run.Artifacts, a => a.Type == "ChangeRequest");
        Assert.Contains(run.Decisions, d => d.Kind == "Replan" && d.Actor == Actor.Agent);
        Assert.Null(designFeedback[1]); // first design attempt had no feedback
        Assert.Contains("needs unapproved dependency", designFeedback[2]); // second attempt received the change request reason
        Assert.True(run.Artifacts.Single(a => a.NodeId == "design" && a.Attempt == 1).Superseded);
    }

    [Fact]
    public async Task Clarification_pauses_for_a_human_then_replans_with_an_extra_gate()
    {
        using var h = new Harness();
        h.Script(StageKind.Requirements, ctx => ctx.Run.ClarificationAnswers.Count == 0
            ? new StageResult
            {
                Outcome = StageOutcome.ClarificationNeeded, Summary = "ambiguous",
                Questions = { new ClarificationQuestion { Id = "Q1", Question = "What does reliable mean?", Options = { "health probes", "backups" }, DefaultAssumption = "health probes and rate limiting", Impact = ImpactLevel.High } }
            }
            : ScriptedAgent.Ok("clarified spec"));

        var run = await h.RunAsync(ScenarioCatalog.Greenfield);

        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Equal("health probes and rate limiting", run.ClarificationAnswers["Q1"]);
        Assert.True(run.Workflow.Has("ops-readiness"), "planner should add the operational readiness gate");
        Assert.Equal(NodeStatus.Succeeded, run.Nodes["ops-readiness"].Status);
        Assert.Equal(ApprovalRule.Always, run.Workflow.Node("implementation").Approval);
        Assert.Contains(run.Decisions, d => d.Kind == "Clarify" && d.Actor == Actor.Human);
        Assert.Contains(run.Decisions, d => d.Kind == "Replan");
    }

    [Fact]
    public async Task Rejected_approval_halts_the_run_without_applying_changes()
    {
        using var h = new Harness { Approvals = new ScriptedApprovalProvider { OnApproval = _ => new ApprovalDecision(false, "reviewer", "scope too large", false) } };
        h.Script(StageKind.Implementation, _ => new StageResult
        {
            Outcome = StageOutcome.Succeeded, Summary = "big change", Impact = ImpactLevel.High,
            ChangeSet = new Workspace.ChangeSet { Summary = "big", Files = { new Workspace.FileChange { Path = "src/Big.cs", Action = "create", Content = "//" } } }
        });

        var run = await h.RunAsync();

        Assert.Equal(RunStatus.Halted, run.Status);
        Assert.Contains("reviewer", run.HaltReason);
        Assert.False(File.Exists(Path.Combine(run.WorkspacePath, "src/Big.cs")), "rejected changes must never reach the workspace");
        Assert.Contains(run.Decisions, d => d.Kind == "Reject" && d.Actor == Actor.Human);
    }

    [Fact]
    public async Task Policy_blocks_secrets_and_the_agent_gets_the_findings_as_feedback()
    {
        using var h = new Harness();
        string? feedback = null;
        h.Script(StageKind.Implementation, ctx =>
        {
            if (ctx.Attempt == 1) return ScriptedAgent.WithChanges("leaky", ("src/Config.cs", "var apiKey = \"sk-ant-abcdefghijklmnopqrstuvwxyz0123\";"));
            feedback = ctx.Feedback;
            return ScriptedAgent.WithChanges("clean", ("src/Config.cs", "var apiKey = Environment.GetEnvironmentVariable(\"API_KEY\");"));
        });

        var run = await h.RunAsync();

        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.True(run.Metrics.PolicyViolations >= 1);
        Assert.Contains("SEC-001", feedback);
        Assert.Contains(run.Decisions, d => d.Kind == "PolicyBlock" && d.Actor == Actor.Policy);
        Assert.DoesNotContain("sk-ant-", File.ReadAllText(Path.Combine(run.WorkspacePath, "src/Config.cs")));
    }

    [Fact]
    public async Task Safe_stop_halts_before_any_work_and_resume_completes()
    {
        using var h = new Harness();
        var orchestrator = h.Build();
        var run = orchestrator.CreateRun(ScenarioCatalog.Greenfield, "replay", autoApprove: false);
        orchestrator.RequestStop(run.RunId);

        run = await orchestrator.ExecuteAsync(run, CancellationToken.None);
        Assert.Equal(RunStatus.Halted, run.Status);
        Assert.Equal(0, run.Metrics.NodesAttempted);

        var resumed = await orchestrator.ResumeAsync(run.RunId, CancellationToken.None);
        Assert.Equal(RunStatus.Completed, resumed.Status);
        Assert.Contains(AuditLog.Read(h.Store.AuditPath(run.RunId)), e => e.Type == AuditTypes.SafeStop);
    }

    [Fact]
    public async Task Injected_failure_exercises_retry_then_rollback()
    {
        using var h = new Harness();
        var run = await h.RunAsync(injectFailure: "design");

        Assert.Equal(RunStatus.Failed, run.Status); // nothing was applied yet, so there is nothing to roll back
        Assert.Equal(3, run.Nodes["design"].Attempts);
        Assert.Equal(2, run.Metrics.Retries);
    }

    [Fact]
    public async Task State_is_persisted_after_every_transition()
    {
        using var h = new Harness();
        var run = await h.RunAsync();
        var reloaded = h.Store.Load(run.RunId);

        Assert.NotNull(reloaded);
        Assert.Equal(RunStatus.Completed, reloaded!.Status);
        Assert.Equal(run.Artifacts.Count, reloaded.Artifacts.Count);
        Assert.Equal(run.Decisions.Count, reloaded.Decisions.Count);
    }

    private sealed class BlockingAgent : IStageAgent
    {
        private readonly TaskCompletionSource _started;
        private readonly Task _release;
        public BlockingAgent(StageKind kind, TaskCompletionSource started, Task release) { Kind = kind; _started = started; _release = release; }
        public StageKind Kind { get; }
        public async Task<StageResult> ExecuteAsync(StageContext ctx, CancellationToken ct)
        {
            _started.TrySetResult();
            await _release;
            return ScriptedAgent.Ok(Kind.ToString());
        }
    }
}
