using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgenticSdlc.Core.Model;

namespace AgenticSdlc.Core.Agents;

/// <summary>
/// Deterministic exit gate: builds and tests the workspace with the real .NET toolchain. No model involved, so the
/// evidence it produces cannot be hallucinated. A failure here triggers a bounded repair cycle on Implementation.
/// </summary>
public sealed class VerificationAgent : IStageAgent
{
    private static readonly Regex SummaryRx = new(@"Failed:\s*(\d+),\s*Passed:\s*(\d+),\s*Skipped:\s*(\d+),\s*Total:\s*(\d+)", RegexOptions.Compiled);
    private static readonly Regex ErrorRx = new(@"error\s+[A-Z]+\d+:.*", RegexOptions.Compiled);

    public StageKind Kind => StageKind.Verification;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(6);

    public async Task<StageResult> ExecuteAsync(StageContext ctx, CancellationToken ct)
    {
        var ws = ctx.Workspace;
        var testProjects = ws.ListFiles(".csproj").Where(p => p.Contains("Tests", StringComparison.OrdinalIgnoreCase)).ToList();
        var allProjects = ws.ListFiles(".csproj");
        if (allProjects.Count == 0)
            return StageResult.Fail("No .csproj found in workspace; nothing to verify.", "Implementation must produce at least one project file.");

        var sw = Stopwatch.StartNew();
        var target = testProjects.FirstOrDefault() ?? allProjects.First();

        // 1) Build (restores as well)
        var build = await RunDotnetAsync(ws.RootPath, $"build \"{target}\" -nologo -v q -clp:NoSummary", ct);
        ctx.Audit("ToolRun", "dotnet build", new() { ["exitCode"] = build.ExitCode, ["ms"] = build.Elapsed.TotalMilliseconds, ["target"] = target });
        if (build.ExitCode != 0)
        {
            var errors = ErrorRx.Matches(build.Output).Select(m => m.Value.Trim()).Distinct().Take(15).ToList();
            if (errors.Count == 0) errors.Add(Tail(build.Output, 1500));
            var content = Report(false, 0, 0, 0, sw.Elapsed, errors, $"dotnet {build.Command}");
            return new StageResult
            {
                Outcome = StageOutcome.Failed,
                ArtifactType = "VerificationReport",
                Summary = $"Build failed with {errors.Count} error(s)",
                Content = content,
                Error = "Build failed",
                Feedback = "The build failed. Fix these compiler errors:\n" + string.Join('\n', errors)
            };
        }

        if (testProjects.Count == 0)
        {
            var content = Report(true, 0, 0, 0, sw.Elapsed, new List<string> { "No test project found" }, $"dotnet {build.Command}");
            return new StageResult
            {
                Outcome = StageOutcome.Failed,
                ArtifactType = "VerificationReport",
                Summary = "Build passed but no tests exist",
                Content = content,
                Error = "No tests",
                Feedback = "No test project exists. The exit gate requires passing tests."
            };
        }

        // 2) Test
        var test = await RunDotnetAsync(ws.RootPath, $"test \"{target}\" --no-build -nologo -v q", ct);
        ctx.Audit("ToolRun", "dotnet test", new() { ["exitCode"] = test.ExitCode, ["ms"] = test.Elapsed.TotalMilliseconds, ["target"] = target });

        int failed = 0, passed = 0, skipped = 0, total = 0;
        var m = SummaryRx.Match(test.Output);
        if (m.Success)
        {
            failed = int.Parse(m.Groups[1].Value);
            passed = int.Parse(m.Groups[2].Value);
            skipped = int.Parse(m.Groups[3].Value);
            total = int.Parse(m.Groups[4].Value);
        }

        var failures = test.Output.Split('\n')
            .Where(l => l.Contains("[FAIL]") || l.TrimStart().StartsWith("Failed ") || l.Contains("Assert.") || l.Contains("Exception"))
            .Select(l => l.Trim()).Distinct().Take(20).ToList();

        var ok = test.ExitCode == 0 && failed == 0 && total > 0;
        var report = Report(true, total, passed, failed, sw.Elapsed, ok ? new List<string>() : failures.Count > 0 ? failures : new List<string> { Tail(test.Output, 1500) }, $"dotnet {test.Command}");

        if (!ok)
        {
            return new StageResult
            {
                Outcome = StageOutcome.Failed,
                ArtifactType = "VerificationReport",
                Summary = total == 0 ? "No tests were discovered" : $"{failed} of {total} tests failed",
                Content = report,
                Error = "Tests failed",
                Feedback = (total == 0 ? "No tests were discovered.\n" : $"{failed} test(s) failed:\n") + string.Join('\n', failures)
            };
        }

        return new StageResult
        {
            Outcome = StageOutcome.Succeeded,
            ArtifactType = "VerificationReport",
            Summary = $"Build passed; {passed}/{total} tests passed in {sw.Elapsed.TotalSeconds:F1}s",
            Content = report,
            Impact = ImpactLevel.Low
        };
    }

    private static JsonElement Report(bool buildOk, int total, int passed, int failed, TimeSpan elapsed, List<string> errors, string command)
        => JsonSerializer.SerializeToElement(new
        {
            buildSucceeded = buildOk,
            testsTotal = total,
            testsPassed = passed,
            testsFailed = failed,
            durationMs = Math.Round(elapsed.TotalMilliseconds),
            errors,
            command,
            toolchain = "dotnet " + Environment.Version
        });

    private static string Tail(string s, int max) => s.Length <= max ? s : s[^max..];

    private sealed record ToolResult(int ExitCode, string Output, TimeSpan Elapsed, string Command);

    private async Task<ToolResult> RunDotnetAsync(string workingDir, string args, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var psi = new ProcessStartInfo("dotnet", args)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        // Build inside a throw-away workspace: never let a stray MSBuild node keep files locked.
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";

        using var p = new Process { StartInfo = psi };
        var output = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* best effort */ }
            lock (output) output.AppendLine(ct.IsCancellationRequested ? "error TOOL001: cancelled by safe-stop" : "error TOOL002: tool timed out");
            return new ToolResult(-1, output.ToString(), sw.Elapsed, args);
        }
        return new ToolResult(p.ExitCode, output.ToString(), sw.Elapsed, args);
    }
}

/// <summary>
/// Deterministic operability checks over the workspace. Added to the plan dynamically when a requirement is clarified
/// to include reliability/operability (demonstrates re-planning), and can be included in any workflow.
/// </summary>
public sealed class OperationalReadinessAgent : IStageAgent
{
    public StageKind Kind => StageKind.OperationalReadiness;

    public Task<StageResult> ExecuteAsync(StageContext ctx, CancellationToken ct)
    {
        var ws = ctx.Workspace;
        var source = string.Join('\n', ws.ListFiles(".cs").Where(f => !f.Contains("Tests")).Select(f => ws.ReadText(f) ?? ""));
        var readme = ws.ReadText("README.md") ?? "";

        var checks = new List<(string Name, bool Passed, string Evidence)>
        {
            ("Liveness health endpoint", source.Contains("/health"), "Looks for a /health route"),
            ("Readiness probe checks the database", source.Contains("/health/ready") || source.Contains("CanConnect"), "Looks for /health/ready or a DB connectivity check"),
            ("Rate limiting configured", source.Contains("AddRateLimiter") || source.Contains("RateLimiter"), "Looks for ASP.NET Core rate limiter registration"),
            ("Graceful shutdown handling", source.Contains("IHostApplicationLifetime") || source.Contains("StopAsync") || source.Contains("BackgroundService"), "Looks for hosted-service shutdown handling"),
            ("Structured problem responses", source.Contains("ProblemDetails") || source.Contains("Results.Problem") || source.Contains("ValidationProblem"), "Looks for RFC 7807 problem details usage"),
            ("Request correlation id", source.Contains("X-Correlation-Id") || source.Contains("TraceIdentifier"), "Looks for correlation id propagation"),
            ("Run instructions documented", readme.Contains("dotnet run", StringComparison.OrdinalIgnoreCase), "README mentions how to run the service"),
        };

        var passedCount = checks.Count(c => c.Passed);
        var content = JsonSerializer.SerializeToElement(new
        {
            passed = passedCount == checks.Count,
            passedCount,
            total = checks.Count,
            checks = checks.Select(c => new { name = c.Name, passed = c.Passed, evidence = c.Evidence })
        });

        var failed = checks.Where(c => !c.Passed).Select(c => c.Name).ToList();
        if (failed.Count > 0)
        {
            return Task.FromResult(new StageResult
            {
                Outcome = StageOutcome.Failed,
                ArtifactType = "OperationalReadinessReport",
                Summary = $"{passedCount}/{checks.Count} operability checks passed",
                Content = content,
                Error = "Operational readiness checks failed",
                Feedback = "Missing operability features: " + string.Join("; ", failed)
            });
        }

        return Task.FromResult(new StageResult
        {
            Outcome = StageOutcome.Succeeded,
            ArtifactType = "OperationalReadinessReport",
            Summary = $"All {checks.Count} operability checks passed",
            Content = content
        });
    }
}

public sealed class AgentRegistry
{
    private readonly Dictionary<StageKind, IStageAgent> _agents;

    public AgentRegistry(IEnumerable<IStageAgent> agents) => _agents = agents.ToDictionary(a => a.Kind);

    public static AgentRegistry Default() => new(new IStageAgent[]
    {
        new CodebaseAnalysisAgent(), new RequirementsAgent(), new DesignAgent(), new ImplementationAgent(),
        new TestAuthoringAgent(), new VerificationAgent(), new DocumentationAgent(), new OperationalReadinessAgent(),
        new ReleaseReadinessAgent()
    });

    public IStageAgent For(StageKind kind) => _agents.TryGetValue(kind, out var a) ? a : throw new InvalidOperationException($"No agent registered for {kind}");

    public AgentRegistry With(IStageAgent replacement)
    {
        var copy = new Dictionary<StageKind, IStageAgent>(_agents) { [replacement.Kind] = replacement };
        return new AgentRegistry(copy.Values);
    }
}
