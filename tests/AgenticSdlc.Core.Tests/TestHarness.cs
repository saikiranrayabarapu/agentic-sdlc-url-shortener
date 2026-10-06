using System.Text.Json;
using AgenticSdlc.Core.Agents;
using AgenticSdlc.Core.Approvals;
using AgenticSdlc.Core.Engine;
using AgenticSdlc.Core.Llm;
using AgenticSdlc.Core.Model;
using AgenticSdlc.Core.Persistence;
using AgenticSdlc.Core.Policy;
using AgenticSdlc.Core.Scenarios;
using AgenticSdlc.Core.Workspace;

namespace AgenticSdlc.Core.Tests;

/// <summary>A stage agent driven by a script, so orchestration behaviour can be tested without any model or toolchain.</summary>
public sealed class ScriptedAgent : IStageAgent
{
    private readonly Func<StageContext, StageResult> _script;

    public ScriptedAgent(StageKind kind, Func<StageContext, StageResult>? script = null)
    {
        Kind = kind;
        _script = script ?? (_ => Ok(kind.ToString()));
    }

    public StageKind Kind { get; }
    public int Calls { get; private set; }

    public Task<StageResult> ExecuteAsync(StageContext ctx, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(_script(ctx));
    }

    public static StageResult Ok(string summary, object? content = null) => new()
    {
        Outcome = StageOutcome.Succeeded,
        ArtifactType = "Fake",
        Summary = summary,
        Content = JsonSerializer.SerializeToElement(content ?? new { ok = true, buildSucceeded = true, testsFailed = 0 })
    };

    public static StageResult WithChanges(string summary, params (string Path, string Content)[] files) => new()
    {
        Outcome = StageOutcome.Succeeded,
        ArtifactType = "FakeChangeSet",
        Summary = summary,
        Content = JsonSerializer.SerializeToElement(new { files = files.Select(f => f.Path) }),
        ChangeSet = new ChangeSet
        {
            Summary = summary,
            Files = files.Select(f => new FileChange { Path = f.Path, Action = "create", Content = f.Content }).ToList()
        }
    };
}

public sealed class NullLlmClient : ILlmClient
{
    public string Name => "null";
    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct) => throw new LlmUnavailableException("No model in unit tests.");
}

public sealed class ScriptedApprovalProvider : IApprovalProvider
{
    public Func<PendingApproval, ApprovalDecision> OnApproval { get; init; } = _ => new ApprovalDecision(true, "tester", null, false);
    public Func<PendingClarification, ClarificationAnswers> OnClarification { get; init; } =
        req => new ClarificationAnswers(req.Questions.ToDictionary(q => q.Id, q => q.DefaultAssumption), "tester", false);

    public Task<ApprovalDecision> RequestApprovalAsync(RunState run, PendingApproval request, CancellationToken ct) => Task.FromResult(OnApproval(request));
    public Task<ClarificationAnswers> RequestClarificationAsync(RunState run, PendingClarification request, CancellationToken ct) => Task.FromResult(OnClarification(request));
}

public sealed class Harness : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "agentic-tests", Guid.NewGuid().ToString("N"));
    public RunStore Store { get; }
    public Dictionary<StageKind, IStageAgent> Agents { get; } = new();
    public IApprovalProvider Approvals { get; set; } = new ScriptedApprovalProvider();

    public Harness()
    {
        Directory.CreateDirectory(Root);
        Store = new RunStore(Path.Combine(Root, "runs"));
        foreach (var kind in Enum.GetValues<StageKind>()) Agents[kind] = new ScriptedAgent(kind);
    }

    public Harness Script(StageKind kind, Func<StageContext, StageResult> script)
    {
        Agents[kind] = new ScriptedAgent(kind, script);
        return this;
    }

    public Orchestrator Build() => new(Store, new AgentRegistry(Agents.Values), new NullLlmClient(), Approvals, new PolicyEngine(),
        new OrchestratorOptions { FixturesRoot = Path.Combine(Root, "fixtures"), MaxRunDuration = TimeSpan.FromMinutes(2) });

    public async Task<RunState> RunAsync(ScenarioDefinition? scenario = null, string? injectFailure = null, Action<Orchestrator, RunState>? beforeExecute = null)
    {
        var orchestrator = Build();
        var run = orchestrator.CreateRun(scenario ?? ScenarioCatalog.Greenfield, "replay", autoApprove: false, injectFailure);
        beforeExecute?.Invoke(orchestrator, run);
        return await orchestrator.ExecuteAsync(run, CancellationToken.None);
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { /* temp */ }
    }
}
