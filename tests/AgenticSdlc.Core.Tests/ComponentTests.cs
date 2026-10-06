using System.Text.Json;
using AgenticSdlc.Core.Llm;
using AgenticSdlc.Core.Model;
using AgenticSdlc.Core.Observability;
using AgenticSdlc.Core.Policy;
using AgenticSdlc.Core.Scenarios;
using AgenticSdlc.Core.Workspace;

namespace AgenticSdlc.Core.Tests;

public sealed class WorkflowDefinitionTests
{
    [Fact]
    public void Planner_graphs_are_valid_and_expose_parallel_waves()
    {
        foreach (var scenario in ScenarioCatalog.All)
        {
            var wf = Planner.BuildInitial(scenario);
            wf.Validate();
            var waves = wf.Waves();
            Assert.Contains(waves, w => w.Contains("implementation") && w.Contains("tests")); // parallel branch
            Assert.Contains(waves, w => w.Contains("verification") && w.Contains("docs"));     // second parallel branch
            Assert.Equal("release", waves[^1].Single());                                       // synchronisation point
        }
    }

    [Fact]
    public void Cycles_and_unknown_dependencies_are_rejected()
    {
        var cyclic = new WorkflowDefinition
        {
            Name = "cyclic",
            Nodes =
            {
                new StageNode { Id = "a", Name = "a", Kind = StageKind.Design, DependsOn = { "b" } },
                new StageNode { Id = "b", Name = "b", Kind = StageKind.Design, DependsOn = { "a" } }
            }
        };
        Assert.Throws<InvalidOperationException>(() => cyclic.Validate());

        var dangling = new WorkflowDefinition { Name = "dangling", Nodes = { new StageNode { Id = "a", Name = "a", Kind = StageKind.Design, DependsOn = { "nope" } } } };
        Assert.Throws<InvalidOperationException>(() => dangling.Validate());
    }

    [Fact]
    public void Transitive_dependents_are_computed()
    {
        var wf = Planner.BuildInitial(ScenarioCatalog.Greenfield);
        var downstream = wf.TransitiveDependents("design");
        Assert.Superset(new HashSet<string> { "implementation", "tests", "verification", "docs", "release" }, downstream);
        Assert.DoesNotContain("requirements", downstream);
    }

    [Fact]
    public void Replan_after_clarification_adds_ops_gate_and_tightens_approval()
    {
        var wf = Planner.BuildInitial(ScenarioCatalog.Ambiguous);
        Assert.Equal(ApprovalRule.IfHighImpact, wf.Node("implementation").Approval);

        var changes = Planner.ReplanAfterClarification(wf, new Dictionary<string, string> { ["Q1"] = "rate limiting and health probes" });

        Assert.Equal(2, changes.Count);
        Assert.True(wf.Has("ops-readiness"));
        Assert.Contains("ops-readiness", wf.Node("release").DependsOn);
        Assert.Equal(ApprovalRule.Always, wf.Node("implementation").Approval);
        wf.Validate();
    }
}

public sealed class PolicyEngineTests
{
    private static Workspace.Workspace TempWorkspace()
    {
        var root = Path.Combine(Path.GetTempPath(), "agentic-policy", Guid.NewGuid().ToString("N"));
        return new Workspace.Workspace(Path.Combine(root, "ws"), Path.Combine(root, "snap"));
    }

    private static ChangeSet Change(string path, string content, string action = "create", string impact = "Low") => new()
    {
        Summary = "t", Impact = impact, Files = { new FileChange { Path = path, Action = action, Content = content } }
    };

    [Theory]
    [InlineData("var k = \"AKIAABCDEFGHIJKLMNOP\";")]
    [InlineData("password = \"hunter2hunter2\"")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----")]
    [InlineData("token: \"ghp_abcdefghijklmnopqrstuvwxyz0123456789\"")]
    public void Secrets_block(string content)
    {
        var result = new PolicyEngine().EvaluateChangeSet(Change("src/A.cs", content), TempWorkspace(), ScenarioKind.Greenfield);
        Assert.True(result.Blocked);
        Assert.Contains(result.Findings, f => f.RuleId == "SEC-001");
    }

    [Fact]
    public void Dangerous_apis_block_in_service_code_but_not_in_tests()
    {
        var engine = new PolicyEngine();
        var service = engine.EvaluateChangeSet(Change("src/Run.cs", "Process.Start(\"cmd\");"), TempWorkspace(), ScenarioKind.Greenfield);
        var tests = engine.EvaluateChangeSet(Change("Tests/RunTests.cs", "Process.Start(\"cmd\");"), TempWorkspace(), ScenarioKind.Greenfield);
        Assert.True(service.Blocked);
        Assert.False(tests.Blocked);
    }

    [Fact]
    public void Unapproved_packages_and_protected_paths_block()
    {
        var engine = new PolicyEngine();
        var pkg = engine.EvaluateChangeSet(Change("Api/Api.csproj", "<PackageReference Include=\"StackExchange.Redis\" Version=\"2.0\" />"), TempWorkspace(), ScenarioKind.Greenfield);
        Assert.Contains(pkg.Findings, f => f.RuleId == "CMP-001" && f.Severity == PolicySeverity.Block);

        var ok = engine.EvaluateChangeSet(Change("Api/Api.csproj", "<PackageReference Include=\"Microsoft.EntityFrameworkCore.Sqlite\" Version=\"10.0.0\" />"), TempWorkspace(), ScenarioKind.Greenfield);
        Assert.False(ok.Blocked);

        var protectedPath = engine.EvaluateChangeSet(Change(".github/workflows/ci.yml", "on: push"), TempWorkspace(), ScenarioKind.Greenfield);
        Assert.Contains(protectedPath.Findings, f => f.RuleId == "CMP-002");

        var escape = engine.EvaluateChangeSet(Change("../outside.txt", "x"), TempWorkspace(), ScenarioKind.Greenfield);
        Assert.True(escape.Blocked);
    }

    [Fact]
    public void Change_control_raises_impact_for_existing_file_edits_and_deletions()
    {
        var engine = new PolicyEngine();
        var ws = TempWorkspace();
        ws.Apply(Change("src/Existing.cs", "// v1"));

        var modify = engine.EvaluateChangeSet(Change("src/Existing.cs", "// v2", "modify"), ws, ScenarioKind.Brownfield);
        Assert.Equal(ImpactLevel.Medium, modify.Impact);
        Assert.Contains(modify.Findings, f => f.RuleId == "CHG-003");

        var delete = engine.EvaluateChangeSet(Change("src/Existing.cs", "", "delete"), ws, ScenarioKind.Brownfield);
        Assert.Equal(ImpactLevel.High, delete.Impact);

        var big = new ChangeSet { Summary = "b", Files = Enumerable.Range(0, 15).Select(i => new FileChange { Path = $"src/F{i}.cs", Action = "create", Content = "//" }).ToList() };
        Assert.Equal(ImpactLevel.High, engine.EvaluateChangeSet(big, ws, ScenarioKind.Brownfield).Impact);
        Assert.Equal(ImpactLevel.Low, engine.EvaluateChangeSet(big, ws, ScenarioKind.Greenfield).Impact); // greenfield creates many files by nature
    }
}

public sealed class AuditLogTests
{
    [Fact]
    public void Hash_chain_verifies_and_detects_tampering()
    {
        var path = Path.Combine(Path.GetTempPath(), "agentic-audit", Guid.NewGuid().ToString("N"), "audit.jsonl");
        using (var log = new AuditLog(path))
        {
            log.Append("r1", AuditTypes.RunCreated, Actor.System, "created");
            log.Append("r1", AuditTypes.NodeStarted, Actor.Agent, "started", "requirements", 1);
            log.Append("r1", AuditTypes.ApprovalDecided, Actor.Human, "approved", "release", 1, new() { ["comment"] = "ship it" }, "alice");
        }
        Assert.True(AuditLog.Verify(path).Ok);
        Assert.Equal(3, AuditLog.Read(path).Count);

        var lines = File.ReadAllLines(path);
        lines[1] = lines[1].Replace("\"message\":\"started\"", "\"message\":\"STARTED-TAMPERED\"");
        File.WriteAllLines(path, lines);

        var (ok, broken) = AuditLog.Verify(path);
        Assert.False(ok);
        Assert.Equal(2, broken);
    }

    [Fact]
    public void Reopening_a_log_continues_the_chain()
    {
        var path = Path.Combine(Path.GetTempPath(), "agentic-audit", Guid.NewGuid().ToString("N"), "audit.jsonl");
        using (var log = new AuditLog(path)) log.Append("r1", "A", Actor.System, "one");
        using (var log = new AuditLog(path)) log.Append("r1", "B", Actor.System, "two");
        var events = AuditLog.Read(path);
        Assert.Equal(2, events[1].Seq);
        Assert.Equal(events[0].Hash, events[1].PrevHash);
        Assert.True(AuditLog.Verify(path).Ok);
    }
}

public sealed class WorkspaceTests
{
    private static Workspace.Workspace New()
    {
        var root = Path.Combine(Path.GetTempPath(), "agentic-ws", Guid.NewGuid().ToString("N"));
        return new Workspace.Workspace(Path.Combine(root, "ws"), Path.Combine(root, "snap"));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("a/../../escape.txt")]
    [InlineData("C:/Windows/evil.txt")]
    public void Paths_cannot_escape_the_workspace(string path)
    {
        Assert.Throws<InvalidOperationException>(() => New().Resolve(path));
    }

    [Fact]
    public void Snapshot_and_restore_round_trip()
    {
        var ws = New();
        ws.Apply(new ChangeSet { Summary = "v1", Files = { new FileChange { Path = "a/one.txt", Action = "create", Content = "1" } } });
        var snap = ws.Snapshot("baseline");
        ws.Apply(new ChangeSet { Summary = "v2", Files = { new FileChange { Path = "a/one.txt", Action = "modify", Content = "2" }, new FileChange { Path = "b/two.txt", Action = "create", Content = "x" } } });
        Assert.Equal("2", ws.ReadText("a/one.txt"));

        ws.Restore(snap);

        Assert.Equal("1", ws.ReadText("a/one.txt"));
        Assert.False(ws.Exists("b/two.txt"));
        Assert.Equal(new[] { "a/one.txt" }, ws.ListFiles());
    }
}

public sealed class LlmPlumbingTests
{
    [Fact]
    public void Json_is_extracted_from_fenced_or_chatty_responses()
    {
        var fenced = new LlmResponse { Text = "Sure! Here you go:\n```json\n{ \"a\": 1 }\n```\nLet me know.", Provider = "x" };
        using var doc = fenced.ParseJson();
        Assert.Equal(1, doc.RootElement.GetProperty("a").GetInt32());

        var bad = new LlmResponse { Text = "no json here", Provider = "x" };
        Assert.Throws<LlmContractException>(() => bad.ParseJson());
    }

    [Fact]
    public async Task Replay_client_expands_content_files_and_prefers_attempt_specific_fixtures()
    {
        var root = Path.Combine(Path.GetTempPath(), "agentic-replay", Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(root, "demo");
        Directory.CreateDirectory(Path.Combine(dir, "files"));
        File.WriteAllText(Path.Combine(dir, "files", "A.cs"), "// hello");
        File.WriteAllText(Path.Combine(dir, "implementation.json"), "{ \"summary\": \"generic\", \"files\": [ { \"path\": \"A.cs\", \"action\": \"create\", \"contentFile\": \"files/A.cs\" } ] }");
        File.WriteAllText(Path.Combine(dir, "implementation.2.json"), "{ \"summary\": \"second attempt\", \"files\": [] }");

        var client = new ReplayLlmClient(root);
        var first = await client.CompleteAsync(Req("implementation", 1), CancellationToken.None);
        var second = await client.CompleteAsync(Req("implementation", 2), CancellationToken.None);

        using var doc = first.ParseJson();
        Assert.Equal("// hello", doc.RootElement.GetProperty("files")[0].GetProperty("content").GetString());
        Assert.False(doc.RootElement.GetProperty("files")[0].TryGetProperty("contentFile", out _));
        Assert.Contains("second attempt", second.Text);
        await Assert.ThrowsAsync<LlmUnavailableException>(() => client.CompleteAsync(Req("design", 1), CancellationToken.None));

        static LlmRequest Req(string node, int attempt) => new() { Scenario = "demo", NodeId = node, Attempt = attempt, SystemPrompt = "", UserPrompt = "", ExpectedSchema = "x" };
    }

    [Fact]
    public void Fallback_client_uses_replay_when_live_provider_is_unavailable()
    {
        var root = Path.Combine(Path.GetTempPath(), "agentic-fallback", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "demo"));
        File.WriteAllText(Path.Combine(root, "demo", "design.json"), "{ \"overview\": \"from fixture\" }");
        var replay = new ReplayLlmClient(root);
        var fallback = new FallbackLlmClient(new NullLlmClient(), replay);
        var fired = false;
        fallback.FellBack += (_, _) => fired = true;

        var response = fallback.CompleteAsync(new LlmRequest { Scenario = "demo", NodeId = "design", Attempt = 1, SystemPrompt = "", UserPrompt = "", ExpectedSchema = "x" }, CancellationToken.None).Result;

        Assert.True(fired);
        Assert.Contains("from fixture", response.Text);
    }
}
