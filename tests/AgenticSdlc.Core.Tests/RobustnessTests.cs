using System.Text.Json;
using AgenticSdlc.Core.Agents;
using AgenticSdlc.Core.Model;
using AgenticSdlc.Core.Observability;
using AgenticSdlc.Core.Scenarios;
using AgenticSdlc.Core.Workspace;

namespace AgenticSdlc.Core.Tests;

public sealed class RunStoreTests
{
    [Fact]
    public async Task Save_retries_while_the_state_file_is_briefly_held_open()
    {
        using var h = new Harness();
        var orchestrator = h.Build();
        var run = orchestrator.CreateRun(ScenarioCatalog.Greenfield, "replay", autoApprove: true);
        var statePath = h.Store.StatePath(run.RunId);

        // Hold the destination open without delete sharing, which is what an antivirus scanner or indexer does on
        // Windows and what makes the atomic replace fail with "access denied". Let go after a short moment.
        var handle = new FileStream(statePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var release = Task.Run(async () =>
        {
            await Task.Delay(120);
            handle.Dispose();
        });

        run.Warnings.Add("saved while locked");
        h.Store.Save(run); // must not throw

        await release;
        var reloaded = h.Store.Load(run.RunId);
        Assert.NotNull(reloaded);
        Assert.Contains("saved while locked", reloaded!.Warnings);
    }
}

public sealed class EngineRobustnessTests
{
    [Fact]
    public async Task An_internal_engine_error_halts_the_run_safely_instead_of_crashing()
    {
        using var h = new Harness();
        // An unknown file action passes the agent contract and the policy gate but makes the workspace apply step throw
        // inside the engine, after the agent has returned: exactly the class of failure that must never take the process
        // down and leave the run stuck in "Running".
        h.Script(StageKind.Implementation, _ => new StageResult
        {
            Outcome = StageOutcome.Succeeded,
            ArtifactType = "Fake",
            Summary = "bad action",
            Content = JsonSerializer.SerializeToElement(new { ok = true }),
            ChangeSet = new ChangeSet { Summary = "bad", Files = { new FileChange { Path = "src/A.cs", Action = "explode", Content = "//" } } }
        });

        var run = await h.RunAsync();

        Assert.Equal(RunStatus.Halted, run.Status);
        Assert.Contains("explode", run.HaltReason);
        Assert.Equal(NodeStatus.Pending, run.Nodes["implementation"].Status); // resumable, nothing half-applied
        Assert.Contains(AuditLog.Read(h.Store.AuditPath(run.RunId)), e => e.Type == AuditTypes.SafeStop);
        Assert.Equal(RunStatus.Halted, h.Store.Load(run.RunId)!.Status); // persisted, not just in memory
    }
}

public sealed class OperationalReadinessAgentTests
{
    private const string RealProgram = """
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddProblemDetails();
        builder.Services.AddRateLimiter(o => { });
        var app = builder.Build();
        app.UseExceptionHandler();
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseRateLimiter();
        app.MapGet("/health", () => Results.Ok());
        app.MapGet("/health/ready", async (AppDbContext db) => await db.Database.CanConnectAsync() ? Results.Ok() : Results.Problem("down", statusCode: 503));
        app.MapPost("/api/links", () => Results.Ok()).RequireRateLimiting("create-link");
        app.Run();

        public sealed class CorrelationIdMiddleware { public const string HeaderName = "X-Correlation-Id"; }

        public sealed class Writer : BackgroundService
        {
            protected override async Task ExecuteAsync(CancellationToken stoppingToken)
            {
                try { await Task.Delay(1, stoppingToken); }
                catch (OperationCanceledException) { /* drain */ }
            }
        }
        """;

    [Fact]
    public async Task Real_registrations_pass_every_check()
    {
        var run = await RunWithProgram(RealProgram);

        Assert.Equal(RunStatus.Completed, run.Status);
        var report = run.Artifacts.Single(a => a.NodeId == "ops-readiness" && !a.Superseded).Content;
        Assert.True(report.GetProperty("passed").GetBoolean());
        Assert.Equal(report.GetProperty("total").GetInt32(), report.GetProperty("passedCount").GetInt32());
    }

    [Fact]
    public async Task Mentions_in_comments_do_not_count()
    {
        // Every keyword is present, but only inside comments: there is no probe, limiter, handler or middleware.
        var commentedOut = string.Join('\n', RealProgram.Split('\n').Select(line => "// " + line));

        var run = await RunWithProgram(commentedOut);

        Assert.NotEqual(RunStatus.Completed, run.Status);
        var failed = run.Nodes["ops-readiness"];
        Assert.Equal(NodeStatus.Failed, failed.Status);
        Assert.Contains("Liveness health endpoint", failed.Feedback); // the failure feedback names every missing feature
    }

    /// <summary>Runs the greenfield graph with a clarification that adds the ops-readiness gate, using the real readiness agent.</summary>
    private static async Task<RunState> RunWithProgram(string programSource)
    {
        using var h = new Harness();
        h.Agents[StageKind.OperationalReadiness] = new OperationalReadinessAgent();
        h.Script(StageKind.Requirements, ctx => ctx.Run.ClarificationAnswers.Count == 0
            ? new StageResult
            {
                Outcome = StageOutcome.ClarificationNeeded, Summary = "ambiguous",
                Questions = { new ClarificationQuestion { Id = "Q1", Question = "Reliable how?", Options = { "health probes and rate limiting" }, DefaultAssumption = "health probes and rate limiting", Impact = ImpactLevel.High } }
            }
            : ScriptedAgent.Ok("spec"));
        h.Script(StageKind.Implementation, _ => ScriptedAgent.WithChanges("service",
            ("UrlShortener.Api/Program.cs", programSource),
            ("README.md", "# Service\n\nRun with `dotnet run --project UrlShortener.Api`.")));
        return await h.RunAsync(ScenarioCatalog.Greenfield);
    }
}
