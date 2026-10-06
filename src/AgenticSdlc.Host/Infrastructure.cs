using System.Collections.Concurrent;
using AgenticSdlc.Core.Agents;
using AgenticSdlc.Core.Approvals;
using AgenticSdlc.Core.Engine;
using AgenticSdlc.Core.Llm;
using AgenticSdlc.Core.Model;
using AgenticSdlc.Core.Observability;
using AgenticSdlc.Core.Persistence;
using AgenticSdlc.Core.Policy;
using AgenticSdlc.Core.Scenarios;

namespace AgenticSdlc.Host;

/// <summary>Locates the repository root (the folder containing the solution) so fixtures and runs resolve the same way from any cwd.</summary>
public static class RepoPaths
{
    public static string Root { get; } = Discover();

    public static string Fixtures => Environment.GetEnvironmentVariable("AGENTIC_FIXTURES") ?? Path.Combine(Root, "fixtures", "replay");
    public static string Runs => Environment.GetEnvironmentVariable("AGENTIC_RUNS") ?? Path.Combine(Root, "runs");
    public static string Dashboard => Path.Combine(Root, "dashboard", "dist", "dashboard", "browser");

    private static string Discover()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "AgenticSdlc.slnx")) || File.Exists(Path.Combine(dir.FullName, "AgenticSdlc.sln"))) return dir.FullName;
                dir = dir.Parent;
            }
        }
        return Directory.GetCurrentDirectory();
    }
}

public sealed record RunRequest(string Scenario, string Mode = "replay", bool AutoApprove = false, string? InjectFailure = null, bool Record = false);

/// <summary>Owns in-flight runs for the HTTP host: one orchestrator per run so each can have its own approval queue and LLM mode.</summary>
public sealed class RunManager
{
    private readonly RunStore _store;
    private readonly ConcurrentDictionary<string, ActiveRun> _active = new();
    private readonly ConcurrentDictionary<string, List<AuditEvent>> _recentEvents = new();

    public RunManager(RunStore store) => _store = store;

    public RunStore Store => _store;

    private sealed record ActiveRun(Orchestrator Orchestrator, QueuedApprovalProvider Approvals, Task Task, CancellationTokenSource Cts);

    public RunState Start(RunRequest request)
    {
        var scenario = ScenarioCatalog.Get(request.Scenario);
        var llm = LlmClientFactory.Create(request.Mode, RepoPaths.Fixtures, request.Record, out var fallback);
        var approvals = new QueuedApprovalProvider { Timeout = TimeSpan.FromHours(2) };
        IApprovalProvider provider = request.AutoApprove ? new AutoApprovalProvider() : approvals;

        var orchestrator = new Orchestrator(_store, AgentRegistry.Default(), llm, provider, new PolicyEngine(), new OrchestratorOptions
        {
            FixturesRoot = RepoPaths.Fixtures,
            AuditSink = ev => _recentEvents.GetOrAdd(ev.RunId, _ => new()).Add(ev)
        });
        if (fallback is not null)
            fallback.FellBack += (req, ex) => _recentEvents.GetOrAdd("_fallbacks", _ => new()).Add(new AuditEvent
            {
                RunId = "_", Type = AuditTypes.Fallback, Actor = Actor.System, Message = $"{req.NodeId}: live provider failed ({ex.Message}); replay fixture used", NodeId = req.NodeId
            });

        var run = orchestrator.CreateRun(scenario, request.Mode, request.AutoApprove, request.InjectFailure);
        var cts = new CancellationTokenSource();
        var task = Task.Run(async () =>
        {
            try { await orchestrator.ExecuteAsync(run, cts.Token); }
            catch (Exception ex)
            {
                run.Warnings.Add($"Unhandled: {ex.Message}");
                run.Status = RunStatus.Failed;
                run.HaltReason = ex.Message;
                _store.Save(run);
            }
            finally { _active.TryRemove(run.RunId, out _); }
        });
        _active[run.RunId] = new ActiveRun(orchestrator, approvals, task, cts);
        return run;
    }

    public RunState Resume(string runId)
    {
        var existing = _store.Load(runId) ?? throw new KeyNotFoundException(runId);
        var llm = LlmClientFactory.Create(existing.Mode, RepoPaths.Fixtures, false, out _);
        var approvals = new QueuedApprovalProvider { Timeout = TimeSpan.FromHours(2) };
        IApprovalProvider provider = existing.AutoApprove ? new AutoApprovalProvider() : approvals;
        var orchestrator = new Orchestrator(_store, AgentRegistry.Default(), llm, provider, new PolicyEngine(), new OrchestratorOptions
        {
            FixturesRoot = RepoPaths.Fixtures,
            AuditSink = ev => _recentEvents.GetOrAdd(ev.RunId, _ => new()).Add(ev)
        });
        var cts = new CancellationTokenSource();
        var task = Task.Run(async () =>
        {
            try { await orchestrator.ResumeAsync(runId, cts.Token); }
            finally { _active.TryRemove(runId, out _); }
        });
        _active[runId] = new ActiveRun(orchestrator, approvals, task, cts);
        return existing;
    }

    public bool IsActive(string runId) => _active.ContainsKey(runId);

    public bool Approve(string runId, string approvalId, bool approved, string approver, string? comment)
        => _active.TryGetValue(runId, out var a) && a.Approvals.Resolve(approvalId, new ApprovalDecision(approved, approver, comment, false));

    public bool Clarify(string runId, string clarificationId, Dictionary<string, string> answers, string approver)
        => _active.TryGetValue(runId, out var a) && a.Approvals.ResolveClarification(clarificationId, new ClarificationAnswers(answers, approver, false));

    public bool Stop(string runId)
    {
        if (!_active.TryGetValue(runId, out var a)) return false;
        a.Orchestrator.RequestStop(runId);
        return true;
    }

    public IReadOnlyList<AuditEvent> Audit(string runId) => AuditLog.Read(_store.AuditPath(runId));
}

/// <summary>Aggregates reliability metrics across every persisted run.</summary>
public static class MetricsAggregator
{
    public static object Aggregate(IReadOnlyList<RunState> runs)
    {
        var finished = runs.Where(r => r.Status is RunStatus.Completed or RunStatus.Failed or RunStatus.RolledBack or RunStatus.Halted).ToList();
        var completed = runs.Count(r => r.Status == RunStatus.Completed);
        var mttrs = runs.Select(r => r.Metrics.MttrMs).Where(m => m.HasValue).Select(m => m!.Value).ToList();
        var e2e = runs.Where(r => r.Metrics.EndToEndLatencyMs.HasValue).Select(r => r.Metrics.EndToEndLatencyMs!.Value).ToList();
        return new
        {
            runs = runs.Count,
            completed,
            runSuccessRate = finished.Count == 0 ? 0 : Math.Round((double)completed / finished.Count, 3),
            nodeSuccessRate = runs.Sum(r => r.Metrics.NodesAttempted) == 0 ? 0 : Math.Round((double)runs.Sum(r => r.Metrics.NodesSucceeded) / runs.Sum(r => r.Metrics.NodesAttempted), 3),
            retries = runs.Sum(r => r.Metrics.Retries),
            repairCycles = runs.Sum(r => r.Metrics.RepairCycles),
            rollbacks = runs.Sum(r => r.Metrics.Rollbacks),
            replans = runs.Sum(r => r.Metrics.Replans),
            policyViolations = runs.Sum(r => r.Metrics.PolicyViolations),
            approvalsRequested = runs.Sum(r => r.Metrics.ApprovalsRequested),
            approvalsGranted = runs.Sum(r => r.Metrics.ApprovalsGranted),
            retryFrequency = runs.Count == 0 ? 0 : Math.Round((double)runs.Sum(r => r.Metrics.Retries + r.Metrics.RepairCycles) / runs.Count, 2),
            rollbackFrequency = runs.Count == 0 ? 0 : Math.Round((double)runs.Sum(r => r.Metrics.Rollbacks) / runs.Count, 2),
            mttrMs = mttrs.Count == 0 ? (double?)null : Math.Round(mttrs.Average(), 1),
            endToEndLatencyMs = new
            {
                avg = e2e.Count == 0 ? (double?)null : Math.Round(e2e.Average(), 1),
                p50 = Percentile(e2e, 0.5),
                p95 = Percentile(e2e, 0.95)
            },
            byStatus = runs.GroupBy(r => r.Status.ToString()).ToDictionary(g => g.Key, g => g.Count()),
            byScenario = runs.GroupBy(r => r.Scenario).ToDictionary(g => g.Key, g => new
            {
                runs = g.Count(),
                completed = g.Count(r => r.Status == RunStatus.Completed),
                avgEndToEndMs = g.Where(r => r.Metrics.EndToEndLatencyMs.HasValue).Select(r => r.Metrics.EndToEndLatencyMs!.Value).DefaultIfEmpty().Average()
            })
        };
    }

    private static double? Percentile(List<double> values, double p)
    {
        if (values.Count == 0) return null;
        var sorted = values.OrderBy(v => v).ToList();
        var idx = (int)Math.Ceiling(p * sorted.Count) - 1;
        return Math.Round(sorted[Math.Clamp(idx, 0, sorted.Count - 1)], 1);
    }
}
