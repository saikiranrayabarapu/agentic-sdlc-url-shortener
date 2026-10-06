using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgenticSdlc.Core.Model;
using AgenticSdlc.Core.Policy;

namespace AgenticSdlc.Core.Agents;

// ---------------------------------------------------------------------------------------------
// Requirements
// ---------------------------------------------------------------------------------------------
public sealed class RequirementsAgent : LlmStageAgent
{
    public override StageKind Kind => StageKind.Requirements;
    protected override string Role => "Requirements Analyst";

    protected override string Contract => """
        {
          "problemStatement": "string",
          "functionalRequirements": ["string"],
          "nonFunctionalRequirements": ["string"],
          "acceptanceCriteria": ["string (testable)"],
          "assumptions": ["string"],
          "outOfScope": ["string"],
          "ambiguities": [ { "id": "Q1", "question": "string", "options": ["string"], "defaultAssumption": "string", "impact": "Low|Medium|High" } ],
          "riskLevel": "Low|Medium|High",
          "needsClarification": true
        }
        Set needsClarification=true ONLY when at least one High-impact ambiguity cannot be safely defaulted.
        """;

    protected override string BuildUserPrompt(StageContext ctx)
    {
        var sb = new StringBuilder();
        var analysis = ctx.UpstreamOfKind(StageKind.CodebaseAnalysis);
        if (analysis is not null)
        {
            sb.AppendLine("# Existing code base analysis");
            sb.AppendLine(ctx.UpstreamJson(StageKind.CodebaseAnalysis));
        }
        sb.AppendLine("# Task");
        sb.AppendLine("Normalise the requirement into a clear engineering problem. Identify ambiguity explicitly rather than guessing.");
        return sb.ToString();
    }

    protected override StageResult Interpret(StageContext ctx, JsonElement root)
    {
        if (!root.TryGetProperty("acceptanceCriteria", out var ac) || ac.GetArrayLength() == 0)
            return StageResult.Fail("Requirement spec has no acceptance criteria.", "Include at least three testable acceptance criteria.");

        var questions = new List<ClarificationQuestion>();
        if (root.TryGetProperty("ambiguities", out var amb) && amb.ValueKind == JsonValueKind.Array)
        {
            foreach (var q in amb.EnumerateArray())
            {
                questions.Add(new ClarificationQuestion
                {
                    Id = Str(q, "id", $"Q{questions.Count + 1}"),
                    Question = Str(q, "question"),
                    Options = Strings(q, "options"),
                    DefaultAssumption = Str(q, "defaultAssumption"),
                    Impact = PolicyEngine.ParseImpact(Str(q, "impact", "Medium"))
                });
            }
        }

        var needsClarification = root.TryGetProperty("needsClarification", out var nc) && nc.ValueKind == JsonValueKind.True;
        var unanswered = questions.Where(q => q.Impact == ImpactLevel.High && !ctx.Run.ClarificationAnswers.ContainsKey(q.Id)).ToList();
        if (needsClarification && unanswered.Count > 0)
        {
            return new StageResult
            {
                Outcome = StageOutcome.ClarificationNeeded,
                ArtifactType = "RequirementSpec",
                Summary = $"{unanswered.Count} high-impact ambiguity(ies) need a human decision",
                Content = root,
                Questions = unanswered
            };
        }

        var risk = PolicyEngine.ParseImpact(Str(root, "riskLevel", "Medium"));
        return new StageResult
        {
            Outcome = StageOutcome.Succeeded,
            ArtifactType = "RequirementSpec",
            Summary = Str(root, "problemStatement"),
            Content = root,
            Impact = risk
        };
    }
}

// ---------------------------------------------------------------------------------------------
// Codebase analysis (brownfield): deterministic inventory + model reasoning about impact
// ---------------------------------------------------------------------------------------------
public sealed class CodebaseAnalysisAgent : LlmStageAgent
{
    private static readonly Regex EndpointRx = new(@"Map(Get|Post|Put|Delete|Patch)\(\s*""([^""]+)""", RegexOptions.Compiled);
    private static readonly Regex ClassRx = new(@"(?:public|internal)\s+(?:sealed\s+)?(?:class|record)\s+(\w+)", RegexOptions.Compiled);
    private static readonly Regex PackageRx = new(@"<PackageReference\s+Include=""([^""]+)""", RegexOptions.Compiled);

    public override StageKind Kind => StageKind.CodebaseAnalysis;
    protected override string Role => "Codebase Analyst";

    protected override string Contract => """
        {
          "summary": "string",
          "architecture": "string",
          "impactedModules": [ { "path": "string", "reason": "string", "changeType": "modify|extend|new" } ],
          "dataFlows": ["string"],
          "apisAffected": ["string"],
          "testsAffected": ["string"],
          "risks": ["string"],
          "recommendedApproach": "string"
        }
        """;

    public static JsonElement Inventory(Workspace.Workspace ws)
    {
        var files = ws.ListFiles();
        var endpoints = new List<object>();
        var types = new List<string>();
        var packages = new List<string>();
        foreach (var f in files)
        {
            var text = ws.ReadText(f) ?? "";
            if (f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                foreach (Match m in EndpointRx.Matches(text)) endpoints.Add(new { method = m.Groups[1].Value.ToUpperInvariant(), route = m.Groups[2].Value, file = f });
                foreach (Match m in ClassRx.Matches(text)) types.Add($"{m.Groups[1].Value} ({f})");
            }
            if (f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                foreach (Match m in PackageRx.Matches(text)) packages.Add(m.Groups[1].Value);
        }
        var inv = new { fileCount = files.Count, files, endpoints, types = types.Distinct().ToList(), packages = packages.Distinct().ToList() };
        return JsonSerializer.SerializeToElement(inv);
    }

    protected override string BuildUserPrompt(StageContext ctx)
    {
        var inv = Inventory(ctx.Workspace);
        var sb = new StringBuilder();
        sb.AppendLine("# Deterministic inventory of the existing workspace (produced by static scan)");
        sb.AppendLine(inv.GetRawText());
        sb.AppendLine("# Task");
        sb.AppendLine("Identify which modules, endpoints, data flows and tests the requirement impacts, and recommend an approach that keeps existing behaviour intact.");
        return sb.ToString();
    }

    protected override StageResult Interpret(StageContext ctx, JsonElement root)
    {
        if (!root.TryGetProperty("impactedModules", out var im) || im.GetArrayLength() == 0)
            return StageResult.Fail("Impact analysis lists no impacted modules.", "List the concrete files/modules that must change.");

        // Merge the deterministic inventory into the artifact so reviewers can see what the model reasoned over.
        var merged = JsonSerializer.SerializeToElement(new
        {
            analysis = root,
            inventory = Inventory(ctx.Workspace)
        });
        return new StageResult
        {
            Outcome = StageOutcome.Succeeded,
            ArtifactType = "ImpactAnalysis",
            Summary = Str(root, "summary"),
            Content = merged,
            Impact = ImpactLevel.Medium
        };
    }
}

// ---------------------------------------------------------------------------------------------
// Design
// ---------------------------------------------------------------------------------------------
public sealed class DesignAgent : LlmStageAgent
{
    public override StageKind Kind => StageKind.Design;
    protected override string Role => "Software Architect";

    protected override string Contract => """
        {
          "overview": "string",
          "components": [ { "name": "string", "responsibility": "string" } ],
          "apiContract": [ { "method": "GET", "path": "/x", "description": "string", "responses": ["200", "404"] } ],
          "dataModel": [ { "entity": "string", "fields": ["string"] } ],
          "decisions": [ { "id": "ADR-1", "decision": "string", "alternatives": ["string"], "rationale": "string", "tradeoffs": "string" } ],
          "filePlan": [ { "path": "string", "purpose": "string" } ],
          "testStrategy": "string",
          "securityConsiderations": ["string"],
          "externalDependencies": ["string (NuGet packages or services required)"]
        }
        """;

    protected override string BuildUserPrompt(StageContext ctx)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Requirement specification");
        sb.AppendLine(ctx.UpstreamJson(StageKind.Requirements));
        if (ctx.UpstreamOfKind(StageKind.CodebaseAnalysis) is not null)
        {
            sb.AppendLine("# Impact analysis of the existing code");
            sb.AppendLine(ctx.UpstreamJson(StageKind.CodebaseAnalysis, 8000));
        }
        sb.AppendLine("# Constraints");
        sb.AppendLine("- ASP.NET Core minimal API on .NET 10, SQLite via EF Core, xUnit tests, no external services unless the requirement demands it.");
        sb.AppendLine("- Dependencies must be on the approved list (Microsoft.*, System.*, xunit, coverlet).");
        sb.AppendLine("# Current workspace files");
        sb.AppendLine(WorkspaceListing(ctx));
        return sb.ToString();
    }

    protected override StageResult Interpret(StageContext ctx, JsonElement root)
    {
        if (!root.TryGetProperty("decisions", out var d) || d.GetArrayLength() == 0)
            return StageResult.Fail("Design has no recorded decisions.", "Record at least one design decision with alternatives and trade-offs.");
        if (!root.TryGetProperty("filePlan", out var fp) || fp.GetArrayLength() == 0)
            return StageResult.Fail("Design has no file plan.", "List the files to create or modify.");
        return new StageResult
        {
            Outcome = StageOutcome.Succeeded,
            ArtifactType = "DesignDoc",
            Summary = Str(root, "overview"),
            Content = root,
            Impact = ImpactLevel.Medium
        };
    }
}

// ---------------------------------------------------------------------------------------------
// Change-set producing agents: Implementation, TestAuthoring, Documentation
// ---------------------------------------------------------------------------------------------
public abstract class ChangeSetAgent : LlmStageAgent
{
    protected override string Contract => """
        {
          "summary": "string",
          "impact": "Low|Medium|High",
          "files": [ { "path": "relative/path.cs", "action": "create|modify|delete", "content": "full file content", "reason": "string" } ],
          "risks": ["string"],
          "assumptions": ["string"],
          "changeRequest": { "targetNodeId": "design", "reason": "string" }
        }
        Use "changeRequest" INSTEAD of "files" only when an upstream artifact is infeasible as specified (e.g. requires an unapproved dependency).
        """;

    protected abstract string ArtifactTypeName { get; }

    protected override StageResult Interpret(StageContext ctx, JsonElement root)
    {
        if (root.TryGetProperty("changeRequest", out var cr) && cr.ValueKind == JsonValueKind.Object)
        {
            var target = Str(cr, "targetNodeId", "design");
            return new StageResult
            {
                Outcome = StageOutcome.ChangeRequest,
                ArtifactType = ArtifactTypeName,
                Summary = $"Change requested on '{target}': {Str(cr, "reason")}",
                Content = root,
                ChangeRequestTargetNodeId = target,
                ChangeRequestReason = Str(cr, "reason")
            };
        }

        var changeSet = ParseChangeSet(root);
        if (changeSet.Files.Count == 0)
            return StageResult.Fail("Change set contains no files.", "Return at least one file change.");
        if (changeSet.Files.Any(f => f.Action != "delete" && string.IsNullOrWhiteSpace(f.Content)))
            return StageResult.Fail("A created/modified file has empty content.", "Provide full file contents for every create/modify entry.");

        // Strip file bodies from the stored artifact; the workspace holds the content and the artifact stays readable.
        var slim = JsonSerializer.SerializeToElement(new
        {
            summary = changeSet.Summary,
            impact = changeSet.Impact,
            files = changeSet.Files.Select(f => new { f.Path, f.Action, f.Reason, bytes = f.Content?.Length ?? 0 }),
            risks = changeSet.Risks,
            assumptions = changeSet.Assumptions
        });

        return new StageResult
        {
            Outcome = StageOutcome.Succeeded,
            ArtifactType = ArtifactTypeName,
            Summary = changeSet.Summary,
            Content = slim,
            ChangeSet = changeSet,
            Impact = PolicyEngine.ParseImpact(changeSet.Impact)
        };
    }
}

public sealed class ImplementationAgent : ChangeSetAgent
{
    public override StageKind Kind => StageKind.Implementation;
    protected override string Role => "Senior Backend Engineer";
    protected override string ArtifactTypeName => "ImplementationChangeSet";

    protected override string BuildUserPrompt(StageContext ctx)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Design to implement");
        sb.AppendLine(ctx.UpstreamJson(StageKind.Design));
        sb.AppendLine("# Acceptance criteria");
        sb.AppendLine(ctx.UpstreamJson(StageKind.Requirements, 6000));
        sb.AppendLine("# Current workspace files");
        sb.AppendLine(WorkspaceListing(ctx));
        sb.AppendLine("# Rules");
        sb.AppendLine("- Produce complete, compiling C# files. Follow the file plan. Do not include secrets. Do not spawn processes.");
        sb.AppendLine("- Keep existing behaviour working; modify existing files minimally and explain each change.");
        return sb.ToString();
    }
}

public sealed class TestAuthoringAgent : ChangeSetAgent
{
    public override StageKind Kind => StageKind.TestAuthoring;
    protected override string Role => "Test Engineer";
    protected override string ArtifactTypeName => "TestChangeSet";

    protected override string BuildUserPrompt(StageContext ctx)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Design (API contract and test strategy)");
        sb.AppendLine(ctx.UpstreamJson(StageKind.Design));
        sb.AppendLine("# Acceptance criteria to cover");
        sb.AppendLine(ctx.UpstreamJson(StageKind.Requirements, 6000));
        sb.AppendLine("# Current workspace files");
        sb.AppendLine(WorkspaceListing(ctx));
        sb.AppendLine("# Rules");
        sb.AppendLine("- xUnit. Integration tests use WebApplicationFactory. Each acceptance criterion maps to at least one test.");
        return sb.ToString();
    }
}

public sealed class DocumentationAgent : ChangeSetAgent
{
    public override StageKind Kind => StageKind.Documentation;
    protected override string Role => "Technical Writer";
    protected override string ArtifactTypeName => "DocumentationChangeSet";

    protected override string BuildUserPrompt(StageContext ctx)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Design");
        sb.AppendLine(ctx.UpstreamJson(StageKind.Design, 8000));
        sb.AppendLine("# Implementation summary");
        sb.AppendLine(ctx.UpstreamJson(StageKind.Implementation, 4000));
        sb.AppendLine("# Current workspace files");
        sb.AppendLine(WorkspaceListing(ctx));
        sb.AppendLine("# Rules");
        sb.AppendLine("- Produce/refresh README.md (run instructions, API reference, configuration) and CHANGELOG.md in the workspace root.");
        return sb.ToString();
    }
}

// ---------------------------------------------------------------------------------------------
// Release readiness: model summarises; orchestrator-supplied evidence is embedded for traceability
// ---------------------------------------------------------------------------------------------
public sealed class ReleaseReadinessAgent : LlmStageAgent
{
    public override StageKind Kind => StageKind.ReleaseReadiness;
    protected override string Role => "Release Manager";

    protected override string Contract => """
        {
          "summary": "string",
          "checklist": [ { "item": "string", "status": "Pass|Warn|Fail", "evidence": "string" } ],
          "riskRegister": [ { "risk": "string", "likelihood": "Low|Medium|High", "impact": "Low|Medium|High", "mitigation": "string" } ],
          "rollbackPlan": "string",
          "knownLimitations": ["string"],
          "recommendation": "Approve|Hold"
        }
        """;

    protected override string BuildUserPrompt(StageContext ctx)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Verification evidence");
        sb.AppendLine(ctx.UpstreamJson(StageKind.Verification));
        sb.AppendLine("# Documentation change summary");
        sb.AppendLine(ctx.UpstreamJson(StageKind.Documentation, 3000));
        if (ctx.UpstreamOfKind(StageKind.OperationalReadiness) is not null)
        {
            sb.AppendLine("# Operational readiness checks");
            sb.AppendLine(ctx.UpstreamJson(StageKind.OperationalReadiness));
        }
        sb.AppendLine("# Decision lineage so far");
        foreach (var d in ctx.Run.Decisions.TakeLast(15))
            sb.AppendLine($"- [{d.At:HH:mm:ss}] {d.Actor} {d.Kind} on {d.NodeId}: {d.Rationale}");
        sb.AppendLine("# Task");
        sb.AppendLine("Assess release readiness. Recommend Hold if any checklist item fails or tests did not pass.");
        return sb.ToString();
    }

    protected override StageResult Interpret(StageContext ctx, JsonElement root)
    {
        var verification = ctx.UpstreamOfKind(StageKind.Verification);
        var testsPassed = verification is not null
                          && verification.Content.TryGetProperty("testsFailed", out var tf) && tf.GetInt32() == 0
                          && verification.Content.TryGetProperty("buildSucceeded", out var bs) && bs.GetBoolean();

        var recommendation = Str(root, "recommendation", "Hold");
        if (recommendation == "Approve" && !testsPassed)
            return StageResult.Fail("Release recommended Approve but verification evidence shows failures.",
                "Verification did not pass; the recommendation must be Hold with the failing items listed.");

        return new StageResult
        {
            Outcome = StageOutcome.Succeeded,
            ArtifactType = "ReleaseReport",
            Summary = $"{recommendation}: {Str(root, "summary")}",
            Content = root,
            Impact = ImpactLevel.High
        };
    }
}
