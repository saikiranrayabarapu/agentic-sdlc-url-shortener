using System.Text.Json;
using AgenticSdlc.Core.Agents;
using AgenticSdlc.Core.Approvals;
using AgenticSdlc.Core.Engine;
using AgenticSdlc.Core.Llm;
using AgenticSdlc.Core.Model;
using AgenticSdlc.Core.Observability;
using AgenticSdlc.Core.Persistence;
using AgenticSdlc.Core.Policy;
using AgenticSdlc.Core.Scenarios;
using AgenticSdlc.Host;
using Microsoft.AspNetCore.Http.Json;

// ---------------------------------------------------------------------------------------------
// Entry point: `dotnet run --project src/AgenticSdlc.Host -- <command> [options]`
//   run <scenario> [--mode replay|anthropic|claude-cli] [--auto-approve] [--inject-failure <nodeId>] [--record]
//   resume <runId>            continue a halted run from persisted state
//   list | show <runId>       inspect runs
//   audit <runId> [--verify]  print the audit trail / verify the hash chain
//   metrics                   aggregated reliability metrics across runs
//   scenarios                 list scenarios
//   serve [--port 5080]       HTTP API + dashboard (default when no command is given)
// ---------------------------------------------------------------------------------------------

var command = args.Length > 0 && !args[0].StartsWith("--") ? args[0].ToLowerInvariant() : "serve";
var opts = Options.Parse(args.Skip(command == "serve" && (args.Length == 0 || args[0].StartsWith("--")) ? 0 : 1).ToArray());
var store = new RunStore(RepoPaths.Runs);

return command switch
{
    "run" => await Cli.RunAsync(store, opts),
    "resume" => await Cli.ResumeAsync(store, opts),
    "list" => Cli.List(store),
    "show" => Cli.Show(store, opts),
    "audit" => Cli.Audit(store, opts),
    "metrics" => Cli.Metrics(store),
    "scenarios" => Cli.Scenarios(),
    "serve" => await Api.ServeAsync(store, opts),
    _ => Cli.Help($"Unknown command '{command}'.")
};

// ---------------------------------------------------------------------------------------------
internal sealed class Options
{
    public string? Positional { get; private set; }
    public string Mode { get; private set; } = "replay";
    public bool AutoApprove { get; private set; }
    public string? InjectFailure { get; private set; }
    public bool Record { get; private set; }
    public bool Verify { get; private set; }
    public int Port { get; private set; } = 5080;

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--mode": o.Mode = args[++i]; break;
                case "--auto-approve": o.AutoApprove = true; break;
                case "--inject-failure": o.InjectFailure = args[++i]; break;
                case "--record": o.Record = true; break;
                case "--verify": o.Verify = true; break;
                case "--port": o.Port = int.Parse(args[++i]); break;
                default:
                    if (!args[i].StartsWith("--")) o.Positional ??= args[i];
                    break;
            }
        }
        return o;
    }
}

internal static class Cli
{
    private static readonly JsonSerializerOptions Pretty = RunState.JsonOptions;

    public static int Help(string? error = null)
    {
        if (error is not null) Console.Error.WriteLine(error);
        Console.WriteLine("""
            Agentic SDLC orchestrator
              run <scenario> [--mode replay|anthropic|claude-cli] [--auto-approve] [--inject-failure <nodeId>] [--record]
              resume <runId>
              list | show <runId> | audit <runId> [--verify] | metrics | scenarios
              serve [--port 5080]
            Scenarios: greenfield, brownfield, ambiguous
            """);
        return error is null ? 0 : 2;
    }

    public static async Task<int> RunAsync(RunStore store, Options o)
    {
        if (o.Positional is null) return Help("Scenario is required.");
        var scenario = ScenarioCatalog.Get(o.Positional);
        var llm = LlmClientFactory.Create(o.Mode, RepoPaths.Fixtures, o.Record, out var fallback);
        IApprovalProvider approvals = o.AutoApprove ? new AutoApprovalProvider() : new ConsoleApprovalProvider();
        var orchestrator = new Orchestrator(store, AgentRegistry.Default(), llm, approvals, new PolicyEngine(), new OrchestratorOptions
        {
            FixturesRoot = RepoPaths.Fixtures,
            AuditSink = PrintEvent
        });
        if (fallback is not null) fallback.FellBack += (req, ex) => Console.WriteLine($"  ⚠ fallback: {req.NodeId} live provider failed ({ex.Message}); using replay fixture");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        Console.WriteLine($"▶ {scenario.Title}");
        Console.WriteLine($"  requirement: {scenario.Requirement}");
        Console.WriteLine($"  mode={o.Mode} auto-approve={o.AutoApprove} inject-failure={o.InjectFailure ?? "none"}");
        Console.WriteLine();

        var run = orchestrator.CreateRun(scenario, o.Mode, o.AutoApprove, o.InjectFailure);
        run = await orchestrator.ExecuteAsync(run, cts.Token);
        PrintSummary(run, store);
        return run.Status == RunStatus.Completed ? 0 : 1;
    }

    public static async Task<int> ResumeAsync(RunStore store, Options o)
    {
        if (o.Positional is null) return Help("Run id is required.");
        var existing = store.Load(o.Positional) ?? throw new ArgumentException($"Run '{o.Positional}' not found.");
        var llm = LlmClientFactory.Create(existing.Mode, RepoPaths.Fixtures, false, out _);
        IApprovalProvider approvals = existing.AutoApprove || o.AutoApprove ? new AutoApprovalProvider() : new ConsoleApprovalProvider();
        var orchestrator = new Orchestrator(store, AgentRegistry.Default(), llm, approvals, new PolicyEngine(), new OrchestratorOptions
        {
            FixturesRoot = RepoPaths.Fixtures,
            AuditSink = PrintEvent
        });
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        var run = await orchestrator.ResumeAsync(o.Positional, cts.Token);
        PrintSummary(run, store);
        return run.Status == RunStatus.Completed ? 0 : 1;
    }

    public static int List(RunStore store)
    {
        Console.WriteLine($"{"RUN ID",-44} {"SCENARIO",-11} {"STATUS",-18} {"NODES",-7} RETRIES/REPAIRS/ROLLBACKS");
        foreach (var r in store.LoadAll())
        {
            var ok = r.Nodes.Values.Count(n => n.Status == NodeStatus.Succeeded);
            Console.WriteLine($"{r.RunId,-44} {r.Scenario,-11} {r.Status,-18} {ok}/{r.Nodes.Count,-5} {r.Metrics.Retries}/{r.Metrics.RepairCycles}/{r.Metrics.Rollbacks}");
        }
        return 0;
    }

    public static int Show(RunStore store, Options o)
    {
        if (o.Positional is null) return Help("Run id is required.");
        var run = store.Load(o.Positional);
        if (run is null) return Help("Run not found.");
        Console.WriteLine(JsonSerializer.Serialize(run, Pretty));
        return 0;
    }

    public static int Audit(RunStore store, Options o)
    {
        if (o.Positional is null) return Help("Run id is required.");
        var path = store.AuditPath(o.Positional);
        if (o.Verify)
        {
            var (ok, broken) = AuditLog.Verify(path);
            Console.WriteLine(ok ? $"✔ audit chain intact ({AuditLog.Read(path).Count} events)" : $"✖ audit chain BROKEN at seq {broken}");
            return ok ? 0 : 1;
        }
        foreach (var ev in AuditLog.Read(path)) PrintEvent(ev);
        return 0;
    }

    public static int Metrics(RunStore store)
    {
        Console.WriteLine(JsonSerializer.Serialize(MetricsAggregator.Aggregate(store.LoadAll()), Pretty));
        return 0;
    }

    public static int Scenarios()
    {
        foreach (var s in ScenarioCatalog.All)
        {
            Console.WriteLine($"{s.Id,-12} {s.Title}");
            Console.WriteLine($"{"",-12} requirement: {s.Requirement}");
            Console.WriteLine($"{"",-12} demonstrates: {s.WhatItDemonstrates}");
            if (s.SeedFromScenarios.Count > 0) Console.WriteLine($"{"",-12} seeded from: {string.Join(", ", s.SeedFromScenarios)}");
            Console.WriteLine();
        }
        return 0;
    }

    private static void PrintEvent(AuditEvent ev)
    {
        var color = ev.Type switch
        {
            AuditTypes.NodeFailed or AuditTypes.PolicyViolation or AuditTypes.Rollback => ConsoleColor.Red,
            AuditTypes.Retry or AuditTypes.RepairCycle or AuditTypes.Warning or AuditTypes.Fallback => ConsoleColor.Yellow,
            AuditTypes.ApprovalRequested or AuditTypes.ClarificationRequested or AuditTypes.SafeStop => ConsoleColor.Magenta,
            AuditTypes.ApprovalDecided or AuditTypes.ClarificationAnswered or AuditTypes.WorkflowReplanned => ConsoleColor.Cyan,
            AuditTypes.NodeSucceeded or AuditTypes.Metrics => ConsoleColor.Green,
            AuditTypes.LlmCall or AuditTypes.EntryGate or AuditTypes.ExitGate or AuditTypes.Snapshot or "ToolRun" => ConsoleColor.DarkGray,
            _ => ConsoleColor.Gray
        };
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine($"{ev.Timestamp:HH:mm:ss.fff} {ev.Seq,4} {ev.Type,-22} {ev.Actor,-6} {(ev.NodeId ?? ""),-15} {ev.Message}");
        Console.ForegroundColor = prev;
    }

    private static void PrintSummary(RunState run, RunStore store)
    {
        var m = run.Metrics;
        Console.WriteLine();
        Console.WriteLine($"═══ {run.Status.ToString().ToUpperInvariant()} ═══ run {run.RunId}");
        if (run.HaltReason is not null) Console.WriteLine($"  reason: {run.HaltReason}");
        foreach (var n in run.Workflow.Nodes)
        {
            var s = run.Nodes[n.Id];
            Console.WriteLine($"  {n.Id,-16} {s.Status,-12} attempts={s.Attempts} {(s.Impact is null ? "" : $"impact={s.Impact}")} {(s.LastError is null ? "" : $"last error: {s.LastError}")}");
        }
        Console.WriteLine($"  metrics: success rate {m.SuccessRate:P0} | retries {m.Retries} | repair cycles {m.RepairCycles} | rollbacks {m.Rollbacks} | re-plans {m.Replans} | policy violations {m.PolicyViolations} | approvals {m.ApprovalsGranted}/{m.ApprovalsRequested} | MTTR {(m.MttrMs is null ? "n/a" : $"{m.MttrMs / 1000.0:F1}s")} | e2e {(m.EndToEndLatencyMs is null ? "n/a" : $"{m.EndToEndLatencyMs / 1000.0:F1}s")}");
        Console.WriteLine($"  decisions: {run.Decisions.Count} (lineage in state.json)   audit: {store.AuditPath(run.RunId)}");
        Console.WriteLine($"  workspace: {run.WorkspacePath}");
    }
}

internal static class Api
{
    public static async Task<int> ServeAsync(RunStore store, Options o)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://localhost:{o.Port}");
        builder.Services.Configure<JsonOptions>(j =>
        {
            j.SerializerOptions.PropertyNamingPolicy = RunState.JsonOptions.PropertyNamingPolicy;
            j.SerializerOptions.DefaultIgnoreCondition = RunState.JsonOptions.DefaultIgnoreCondition;
            foreach (var c in RunState.JsonOptions.Converters) j.SerializerOptions.Converters.Add(c);
        });
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton<RunManager>();
        builder.Services.AddCors(c => c.AddDefaultPolicy(p => p.WithOrigins("http://localhost:4200").AllowAnyHeader().AllowAnyMethod()));
        builder.Services.AddOpenApi();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        var app = builder.Build();
        app.UseCors();
        app.MapOpenApi();

        var api = app.MapGroup("/api");

        api.MapGet("/scenarios", () => ScenarioCatalog.All.Select(s => new
        {
            s.Id, s.Title, Kind = s.Kind.ToString(), s.Requirement, s.WhatItDemonstrates, s.SeedFromScenarios,
            workflow = Planner.BuildInitial(s).Nodes.Select(n => new { n.Id, n.Name, Kind = n.Kind.ToString(), n.DependsOn, Approval = n.Approval.ToString(), n.MaxRetries, n.EntryGate, n.ExitGate })
        }));
        api.MapGet("/modes", () => LlmClientFactory.Modes);

        api.MapGet("/runs", (RunStore s) => s.LoadAll().Select(Summary));
        api.MapPost("/runs", (RunManager mgr, RunRequest req) =>
        {
            try { return Results.Created($"/api/runs/{"x"}", mgr.Start(req)); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (LlmUnavailableException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
        api.MapGet("/runs/{id}", (RunStore s, string id) => s.Load(id) is { } run ? Results.Ok(run) : Results.NotFound());
        api.MapGet("/runs/{id}/audit", (RunManager mgr, string id, int? after) => mgr.Audit(id).Where(e => after is null || e.Seq > after));
        api.MapGet("/runs/{id}/audit/verify", (RunStore s, string id) =>
        {
            var (ok, broken) = AuditLog.Verify(s.AuditPath(id));
            return new { ok, brokenAtSeq = broken, events = AuditLog.Read(s.AuditPath(id)).Count };
        });
        api.MapGet("/runs/{id}/artifacts/{artifactId}", (RunStore s, string id, string artifactId) =>
            s.Load(id)?.Artifacts.FirstOrDefault(a => a.Id == artifactId) is { } a ? Results.Ok(a) : Results.NotFound());
        api.MapGet("/runs/{id}/workspace", (RunStore s, string id) =>
        {
            var run = s.Load(id);
            if (run is null) return Results.NotFound();
            var ws = new AgenticSdlc.Core.Workspace.Workspace(run.WorkspacePath, s.SnapshotsPath(id));
            return Results.Ok(ws.ListFiles());
        });
        api.MapGet("/runs/{id}/workspace/file", (RunStore s, string id, string path) =>
        {
            var run = s.Load(id);
            if (run is null) return Results.NotFound();
            var ws = new AgenticSdlc.Core.Workspace.Workspace(run.WorkspacePath, s.SnapshotsPath(id));
            try { return ws.ReadText(path) is { } text ? Results.Text(text) : Results.NotFound(); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
        api.MapPost("/runs/{id}/approvals/{approvalId}", (RunManager mgr, string id, string approvalId, ApprovalBody body) =>
            mgr.Approve(id, approvalId, body.Approved, string.IsNullOrWhiteSpace(body.Approver) ? "dashboard-user" : body.Approver, body.Comment)
                ? Results.Accepted()
                : Results.Conflict(new { error = "No pending approval with that id on an active run." }));
        api.MapPost("/runs/{id}/clarifications/{clarificationId}", (RunManager mgr, string id, string clarificationId, ClarificationBody body) =>
            mgr.Clarify(id, clarificationId, body.Answers, string.IsNullOrWhiteSpace(body.Approver) ? "dashboard-user" : body.Approver)
                ? Results.Accepted()
                : Results.Conflict(new { error = "No pending clarification with that id on an active run." }));
        api.MapPost("/runs/{id}/stop", (RunManager mgr, string id) => mgr.Stop(id) ? Results.Accepted() : Results.Conflict(new { error = "Run is not active." }));
        api.MapPost("/runs/{id}/resume", (RunManager mgr, string id) =>
        {
            try { return Results.Accepted(null, mgr.Resume(id)); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        });
        api.MapGet("/metrics", (RunStore s) => MetricsAggregator.Aggregate(s.LoadAll()));
        api.MapGet("/health", () => new { status = "ok", repoRoot = RepoPaths.Root, fixtures = Directory.Exists(RepoPaths.Fixtures) });

        // Dashboard (Angular build output) with SPA fallback.
        if (Directory.Exists(RepoPaths.Dashboard))
        {
            var fileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(RepoPaths.Dashboard);
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = fileProvider });
            app.UseStaticFiles(new StaticFileOptions { FileProvider = fileProvider });
            app.MapFallback(async ctx =>
            {
                ctx.Response.ContentType = "text/html";
                await ctx.Response.SendFileAsync(Path.Combine(RepoPaths.Dashboard, "index.html"));
            });
        }
        else
        {
            app.MapGet("/", () => Results.Text(
                "Agentic SDLC API is running. Dashboard not built yet: run `npm install && npm run build` in /dashboard, or use `ng serve` on port 4200.\nAPI: /api/scenarios, /api/runs, /api/metrics, /openapi/v1.json"));
        }

        Console.WriteLine($"Agentic SDLC host listening on http://localhost:{o.Port}  (runs: {RepoPaths.Runs}, fixtures: {RepoPaths.Fixtures}, dashboard: {(Directory.Exists(RepoPaths.Dashboard) ? "built" : "not built")})");
        await app.RunAsync();
        return 0;
    }

    private static object Summary(RunState r) => new
    {
        r.RunId, r.Scenario, r.ScenarioKind, r.Mode, r.Status, r.CreatedAt, r.UpdatedAt, r.CompletedAt, r.HaltReason, r.AutoApprove,
        nodes = r.Nodes.Values.Select(n => new { n.NodeId, n.Status, n.Attempts, n.Impact }),
        pendingApprovals = r.PendingApprovals.Count,
        pendingClarification = r.PendingClarification is not null,
        metrics = r.Metrics
    };

    public sealed record ApprovalBody(bool Approved, string? Approver, string? Comment);
    public sealed record ClarificationBody(Dictionary<string, string> Answers, string? Approver);
}
