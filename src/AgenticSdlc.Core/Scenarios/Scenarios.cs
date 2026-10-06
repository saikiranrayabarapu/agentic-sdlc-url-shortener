using AgenticSdlc.Core.Model;

namespace AgenticSdlc.Core.Scenarios;

public sealed class ScenarioDefinition
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required ScenarioKind Kind { get; init; }
    public required string Requirement { get; init; }
    /// <summary>Scenarios whose recorded Implementation/TestAuthoring/Documentation outputs are applied first to seed the workspace.</summary>
    public List<string> SeedFromScenarios { get; init; } = new();
    public string WhatItDemonstrates { get; init; } = "";
}

public static class ScenarioCatalog
{
    public static readonly ScenarioDefinition Greenfield = new()
    {
        Id = "greenfield",
        Title = "Greenfield: build the URL shortener service",
        Kind = ScenarioKind.Greenfield,
        Requirement =
            "Build a URL shortener HTTP service. Users submit a long URL and receive a short code; visiting the short URL " +
            "redirects to the original. Support optional custom aliases and optional expiry. Expose a health endpoint. " +
            "Reject invalid or non-http(s) URLs. Store links durably in SQLite. Include automated tests and a README.",
        WhatItDemonstrates = "Full lifecycle from an empty workspace: requirements -> design -> parallel implementation/tests -> verification -> docs -> release approval."
    };

    public static readonly ScenarioDefinition Brownfield = new()
    {
        Id = "brownfield",
        Title = "Brownfield: add click analytics to the existing shortener",
        Kind = ScenarioKind.Brownfield,
        Requirement =
            "Add click analytics to the existing URL shortener. Every redirect must record a click (timestamp, referrer, " +
            "user-agent) without slowing down the redirect. Expose GET /api/links/{code}/stats returning total clicks, " +
            "clicks per day for the last 7 days, and the top 5 referrers. Existing behaviour and tests must keep working.",
        SeedFromScenarios = { "greenfield" },
        WhatItDemonstrates = "Codebase impact analysis, minimal modification of existing files, regression gate, a failed attempt repaired through the bounded retry loop, human approval of a Medium/High-impact change."
    };

    public static readonly ScenarioDefinition Ambiguous = new()
    {
        Id = "ambiguous",
        Title = "Ambiguous: \"make the service more reliable\"",
        Kind = ScenarioKind.Ambiguous,
        Requirement = "Make the URL shortener more reliable.",
        SeedFromScenarios = { "greenfield", "brownfield" },
        WhatItDemonstrates = "Ambiguity detection with a human clarification checkpoint, dynamic re-planning (new stage added, approval policy tightened), and a design change request raised by implementation that invalidates and re-runs the design stage."
    };

    public static IReadOnlyList<ScenarioDefinition> All { get; } = new[] { Greenfield, Brownfield, Ambiguous };

    public static ScenarioDefinition Get(string id)
        => All.FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
           ?? throw new ArgumentException($"Unknown scenario '{id}'. Available: {string.Join(", ", All.Select(s => s.Id))}");
}

/// <summary>
/// Builds the initial dependency graph for a scenario and re-plans it when upstream outputs change.
/// The graph is explicit data, not code flow, so it can be inspected, audited and modified at run time.
/// </summary>
public static class Planner
{
    public static WorkflowDefinition BuildInitial(ScenarioDefinition scenario)
    {
        var nodes = new List<StageNode>();
        var requirementsDeps = new List<string>();

        if (scenario.Kind != ScenarioKind.Greenfield)
        {
            nodes.Add(new StageNode
            {
                Id = "analysis", Name = "Codebase analysis", Kind = StageKind.CodebaseAnalysis,
                ExitGate = "At least one impacted module identified; inventory attached"
            });
            requirementsDeps.Add("analysis");
        }

        nodes.Add(new StageNode
        {
            Id = "requirements", Name = "Requirements", Kind = StageKind.Requirements, DependsOn = requirementsDeps,
            MaxRetries = 2,
            ExitGate = "Testable acceptance criteria present; no unanswered High-impact ambiguity"
        });
        nodes.Add(new StageNode
        {
            Id = "design", Name = "Architecture & design", Kind = StageKind.Design, DependsOn = { "requirements" },
            MaxRetries = 2,
            ExitGate = "Decisions with trade-offs recorded; file plan present; dependencies on approved list"
        });
        nodes.Add(new StageNode
        {
            Id = "implementation", Name = "Implementation", Kind = StageKind.Implementation, DependsOn = { "design" },
            MaxRetries = 2,
            // Brownfield changes to a known code base always need sign-off. Greenfield/ambiguous start risk-based;
            // the planner tightens this to Always once an ambiguous requirement has been clarified (see ReplanAfterClarification).
            Approval = scenario.Kind == ScenarioKind.Brownfield ? ApprovalRule.Always : ApprovalRule.IfHighImpact,
            EntryGate = "Design approved; workspace snapshot taken",
            ExitGate = "Policy guardrails pass (secrets, dangerous APIs, dependency allow-list, protected paths); approval if required"
        });
        nodes.Add(new StageNode
        {
            Id = "tests", Name = "Test authoring", Kind = StageKind.TestAuthoring, DependsOn = { "design" },
            MaxRetries = 2,
            ExitGate = "Policy guardrails pass; at least one test file produced"
        });
        nodes.Add(new StageNode
        {
            Id = "verification", Name = "Build & test verification", Kind = StageKind.Verification, DependsOn = { "implementation", "tests" },
            MaxRetries = 0,
            EntryGate = "Implementation and tests both applied (synchronisation point)",
            ExitGate = "dotnet build succeeds and all tests pass"
        });
        nodes.Add(new StageNode
        {
            Id = "docs", Name = "Documentation", Kind = StageKind.Documentation, DependsOn = { "implementation", "tests" },
            MaxRetries = 1,
            ExitGate = "README and CHANGELOG produced"
        });
        nodes.Add(new StageNode
        {
            Id = "release", Name = "Release readiness", Kind = StageKind.ReleaseReadiness, DependsOn = { "verification", "docs" },
            MaxRetries = 1,
            Approval = ApprovalRule.Always,
            EntryGate = "Verification passed and documentation present",
            ExitGate = "Checklist, risk register and rollback plan produced; human sign-off"
        });

        var wf = new WorkflowDefinition { Name = $"{scenario.Id}-v1", Nodes = nodes };
        wf.Validate();
        return wf;
    }

    /// <summary>
    /// Re-plan after a clarification. Returns a human-readable list of changes, or empty when nothing changed.
    /// Rules are explicit so the re-planning itself is auditable:
    ///  - If the clarified scope includes operability/reliability work, add an OperationalReadiness gate before release.
    ///  - Any clarified ambiguous requirement makes Implementation approval mandatory.
    /// </summary>
    public static List<string> ReplanAfterClarification(WorkflowDefinition wf, IReadOnlyDictionary<string, string> answers)
    {
        var changes = new List<string>();
        var joined = string.Join(' ', answers.Values).ToLowerInvariant();

        var impl = wf.Node("implementation");
        if (impl.Approval != ApprovalRule.Always)
        {
            impl.Approval = ApprovalRule.Always;
            changes.Add("implementation.approval: IfHighImpact -> Always (requirement was ambiguous; human must confirm scope before changes apply)");
        }

        var wantsOperability = joined.Contains("health") || joined.Contains("rate") || joined.Contains("graceful")
                               || joined.Contains("resilien") || joined.Contains("operab") || joined.Contains("availab")
                               || joined.Contains("reliab");
        if (wantsOperability && !wf.Has("ops-readiness"))
        {
            wf.Nodes.Add(new StageNode
            {
                Id = "ops-readiness", Name = "Operational readiness checks", Kind = StageKind.OperationalReadiness,
                DependsOn = { "implementation", "tests" },
                MaxRetries = 0,
                ExitGate = "Health, readiness, rate limiting, graceful shutdown, problem details, correlation id, run docs all present"
            });
            wf.Node("release").DependsOn.Add("ops-readiness");
            changes.Add("+ node ops-readiness (OperationalReadiness) after implementation/tests; release now depends on it");
        }

        wf.Validate();
        return changes;
    }
}
