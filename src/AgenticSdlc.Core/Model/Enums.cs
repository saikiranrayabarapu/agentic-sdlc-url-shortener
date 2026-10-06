namespace AgenticSdlc.Core.Model;

/// <summary>Lifecycle of a whole orchestration run.</summary>
public enum RunStatus
{
    Planned,
    Running,
    AwaitingApproval,
    AwaitingClarification,
    Completed,
    /// <summary>Stopped safely by a human, a policy, or a safe-stop signal. Resumable.</summary>
    Halted,
    /// <summary>Unrecoverable failure; workspace restored to the last good snapshot.</summary>
    RolledBack,
    Failed
}

/// <summary>Lifecycle of a single node (stage) in the dependency graph.</summary>
public enum NodeStatus
{
    Pending,
    Running,
    AwaitingApproval,
    Succeeded,
    Failed,
    Skipped,
    /// <summary>A downstream stage requested a change, or an upstream output changed. Will be re-executed.</summary>
    Invalidated
}

public enum StageKind
{
    CodebaseAnalysis,
    Requirements,
    Design,
    Implementation,
    TestAuthoring,
    Verification,
    Documentation,
    OperationalReadiness,
    ReleaseReadiness
}

public enum ApprovalRule
{
    None,
    Always,
    IfHighImpact
}

public enum ImpactLevel
{
    Low,
    Medium,
    High
}

public enum Actor
{
    System,
    Agent,
    Human,
    Policy
}

public enum ScenarioKind
{
    Greenfield,
    Brownfield,
    Ambiguous
}
