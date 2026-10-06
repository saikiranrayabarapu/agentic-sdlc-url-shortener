using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgenticSdlc.Core.Model;

public sealed class NodeState
{
    public required string NodeId { get; init; }
    public NodeStatus Status { get; set; } = NodeStatus.Pending;
    public int Attempts { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? LastError { get; set; }
    /// <summary>Feedback injected into the next attempt (e.g. compiler errors, failing tests, policy violations).</summary>
    public string? Feedback { get; set; }
    public List<string> ArtifactIds { get; set; } = new();
    public ImpactLevel? Impact { get; set; }
    public bool ApprovalGranted { get; set; }
}

/// <summary>Typed, versioned output of a stage. Carries provenance so downstream decisions can be traced to their inputs.</summary>
public sealed class Artifact
{
    public required string Id { get; init; }
    public required string NodeId { get; init; }
    public required int Attempt { get; init; }
    public required string Type { get; init; }
    public required string Summary { get; init; }
    public required JsonElement Content { get; init; }
    /// <summary>SHA-256 of the inputs that produced this artifact. Changing inputs changes the hash, which drives re-planning.</summary>
    public required string InputsHash { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool Superseded { get; set; }
}

/// <summary>A governance decision. The chain of decisions is the "decision lineage" of the run.</summary>
public sealed class Decision
{
    public required string Id { get; init; }
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
    public required Actor Actor { get; init; }
    public string? ActorName { get; init; }
    public required string NodeId { get; init; }
    /// <summary>Approve, Reject, Retry, Repair, Rollback, Replan, Clarify, PolicyBlock, Fallback, SafeStop.</summary>
    public required string Kind { get; init; }
    public required string Rationale { get; init; }
    public List<string> BasedOnArtifactIds { get; init; } = new();
}

public sealed class PendingApproval
{
    public required string Id { get; init; }
    public required string NodeId { get; init; }
    public required string Title { get; init; }
    public required string Summary { get; init; }
    public required ImpactLevel Impact { get; init; }
    public List<string> ArtifactIds { get; init; } = new();
    public DateTimeOffset RequestedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class ClarificationQuestion
{
    public required string Id { get; init; }
    public required string Question { get; init; }
    public List<string> Options { get; init; } = new();
    public required string DefaultAssumption { get; init; }
    public ImpactLevel Impact { get; init; } = ImpactLevel.Medium;
}

public sealed class PendingClarification
{
    public required string Id { get; init; }
    public required string NodeId { get; init; }
    public List<ClarificationQuestion> Questions { get; init; } = new();
    public DateTimeOffset RequestedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class RecoveryWindow
{
    public required string NodeId { get; init; }
    public DateTimeOffset FailedAt { get; init; }
    public DateTimeOffset? RecoveredAt { get; set; }
    public string Kind { get; init; } = "Retry";
}

public sealed class RunMetrics
{
    public int NodesAttempted { get; set; }
    public int NodesSucceeded { get; set; }
    public int NodesFailed { get; set; }
    public int Retries { get; set; }
    public int RepairCycles { get; set; }
    public int Rollbacks { get; set; }
    public int Fallbacks { get; set; }
    public int Replans { get; set; }
    public int PolicyViolations { get; set; }
    public int ApprovalsRequested { get; set; }
    public int ApprovalsGranted { get; set; }
    public int LlmCalls { get; set; }
    public double? EndToEndLatencyMs { get; set; }
    public Dictionary<string, double> StageLatencyMs { get; set; } = new();
    public List<RecoveryWindow> Recoveries { get; set; } = new();

    public double SuccessRate => NodesAttempted == 0 ? 0 : Math.Round((double)NodesSucceeded / NodesAttempted, 3);

    /// <summary>Mean time to recovery: average time between a failure and the subsequent successful recovery.</summary>
    public double? MttrMs
    {
        get
        {
            var recovered = Recoveries.Where(r => r.RecoveredAt.HasValue).ToList();
            if (recovered.Count == 0) return null;
            return Math.Round(recovered.Average(r => (r.RecoveredAt!.Value - r.FailedAt).TotalMilliseconds), 1);
        }
    }
}

public sealed class RunState
{
    public required string RunId { get; init; }
    public required string Scenario { get; init; }
    public required ScenarioKind ScenarioKind { get; init; }
    public required string Mode { get; init; }
    public required string RequirementText { get; init; }
    public required string WorkspacePath { get; init; }
    public RunStatus Status { get; set; } = RunStatus.Planned;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public required WorkflowDefinition Workflow { get; set; }
    public Dictionary<string, NodeState> Nodes { get; init; } = new();
    public List<Artifact> Artifacts { get; init; } = new();
    public List<Decision> Decisions { get; init; } = new();
    public List<PendingApproval> PendingApprovals { get; init; } = new();
    public PendingClarification? PendingClarification { get; set; }
    public Dictionary<string, string> ClarificationAnswers { get; init; } = new();
    public RunMetrics Metrics { get; init; } = new();
    public int RepairCyclesUsed { get; set; }
    public int ReplansUsed { get; set; }
    public List<string> Warnings { get; init; } = new();
    public string? HaltReason { get; set; }
    public bool AutoApprove { get; init; }
    public string? InjectFailureNodeId { get; init; }
    public List<string> SnapshotIds { get; init; } = new();

    public NodeState Node(string id) => Nodes[id];

    public IEnumerable<Artifact> ArtifactsOf(string nodeId) => Artifacts.Where(a => a.NodeId == nodeId && !a.Superseded);

    public Artifact? LatestArtifact(string nodeId) => ArtifactsOf(nodeId).OrderByDescending(a => a.Attempt).FirstOrDefault();

    /// <summary>Shared serializer settings: camelCase + string enums, used for persisted state and the HTTP API alike.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
