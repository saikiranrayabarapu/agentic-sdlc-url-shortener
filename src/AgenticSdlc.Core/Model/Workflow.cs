namespace AgenticSdlc.Core.Model;

/// <summary>
/// A node in the explicit dependency graph. Nodes become eligible to run only when every
/// dependency has succeeded (entry gate) and are considered done only when their exit gate passes.
/// </summary>
public sealed class StageNode
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required StageKind Kind { get; init; }
    public List<string> DependsOn { get; init; } = new();

    /// <summary>Bounded retries for this node's own failures (agent errors, policy violations, exit-gate failures).</summary>
    public int MaxRetries { get; init; } = 1;

    public ApprovalRule Approval { get; set; } = ApprovalRule.None;

    /// <summary>Human-readable description of what the exit gate checks. Used in the audit trail and dashboard.</summary>
    public string ExitGate { get; init; } = "Artifact produced and schema-valid";

    public string EntryGate { get; init; } = "All dependencies succeeded";
}

public sealed class WorkflowDefinition
{
    public required string Name { get; init; }
    public List<StageNode> Nodes { get; init; } = new();

    /// <summary>Shared budget for cross-node repair loops (e.g. Verification failure re-running Implementation).</summary>
    public int MaxRepairCycles { get; init; } = 2;

    /// <summary>Upper bound on dynamic re-plans (design change requests, clarification-driven re-planning).</summary>
    public int MaxReplans { get; init; } = 2;

    public StageNode Node(string id) => Nodes.First(n => n.Id == id);

    public bool Has(string id) => Nodes.Any(n => n.Id == id);

    public IEnumerable<StageNode> Dependents(string id) => Nodes.Where(n => n.DependsOn.Contains(id));

    /// <summary>All nodes transitively downstream of <paramref name="id"/>.</summary>
    public HashSet<string> TransitiveDependents(string id)
    {
        var result = new HashSet<string>();
        var queue = new Queue<string>(Dependents(id).Select(n => n.Id));
        while (queue.Count > 0)
        {
            var next = queue.Dequeue();
            if (!result.Add(next)) continue;
            foreach (var d in Dependents(next)) queue.Enqueue(d.Id);
        }
        return result;
    }

    /// <summary>Validates the graph: unique ids, known dependencies, acyclic.</summary>
    public void Validate()
    {
        var ids = new HashSet<string>();
        foreach (var n in Nodes)
        {
            if (!ids.Add(n.Id)) throw new InvalidOperationException($"Duplicate node id '{n.Id}'.");
        }
        foreach (var n in Nodes)
        {
            foreach (var d in n.DependsOn)
            {
                if (!ids.Contains(d)) throw new InvalidOperationException($"Node '{n.Id}' depends on unknown node '{d}'.");
            }
        }
        // Kahn's algorithm for cycle detection.
        var indegree = Nodes.ToDictionary(n => n.Id, n => n.DependsOn.Count);
        var ready = new Queue<string>(indegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        var visited = 0;
        while (ready.Count > 0)
        {
            var id = ready.Dequeue();
            visited++;
            foreach (var dep in Dependents(id))
            {
                if (--indegree[dep.Id] == 0) ready.Enqueue(dep.Id);
            }
        }
        if (visited != Nodes.Count) throw new InvalidOperationException("Workflow graph contains a cycle.");
    }

    /// <summary>Topological "waves": nodes in the same wave have no dependencies on each other and may run in parallel.</summary>
    public List<List<string>> Waves()
    {
        var remaining = Nodes.ToDictionary(n => n.Id, n => new HashSet<string>(n.DependsOn));
        var waves = new List<List<string>>();
        while (remaining.Count > 0)
        {
            var wave = remaining.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).OrderBy(x => x).ToList();
            if (wave.Count == 0) throw new InvalidOperationException("Workflow graph contains a cycle.");
            waves.Add(wave);
            foreach (var id in wave) remaining.Remove(id);
            foreach (var kv in remaining) kv.Value.ExceptWith(wave);
        }
        return waves;
    }
}
