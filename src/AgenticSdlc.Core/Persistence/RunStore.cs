using System.Text.Json;
using AgenticSdlc.Core.Model;

namespace AgenticSdlc.Core.Persistence;

/// <summary>
/// File-based persistence for run state. State is written after every transition so a run can be
/// inspected while in flight, resumed after a safe-stop, or audited after completion.
/// Layout: {root}/{runId}/state.json, audit.jsonl, workspace/, snapshots/
/// </summary>
public sealed class RunStore
{
    private readonly object _lock = new();

    public RunStore(string rootPath)
    {
        RootPath = Path.GetFullPath(rootPath);
        Directory.CreateDirectory(RootPath);
    }

    public string RootPath { get; }

    public string RunDirectory(string runId) => Path.Combine(RootPath, runId);
    public string StatePath(string runId) => Path.Combine(RunDirectory(runId), "state.json");
    public string AuditPath(string runId) => Path.Combine(RunDirectory(runId), "audit.jsonl");
    public string WorkspacePath(string runId) => Path.Combine(RunDirectory(runId), "workspace");
    public string SnapshotsPath(string runId) => Path.Combine(RunDirectory(runId), "snapshots");
    public string StopSignalPath(string runId) => Path.Combine(RunDirectory(runId), "STOP");

    public void Save(RunState state)
    {
        lock (_lock)
        {
            state.UpdatedAt = DateTimeOffset.UtcNow;
            Directory.CreateDirectory(RunDirectory(state.RunId));
            var tmp = StatePath(state.RunId) + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, RunState.JsonOptions));
            File.Move(tmp, StatePath(state.RunId), overwrite: true);
        }
    }

    public RunState? Load(string runId)
    {
        var path = StatePath(runId);
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<RunState>(File.ReadAllText(path), RunState.JsonOptions);
    }

    public IReadOnlyList<RunState> LoadAll()
    {
        if (!Directory.Exists(RootPath)) return Array.Empty<RunState>();
        var list = new List<RunState>();
        foreach (var dir in Directory.GetDirectories(RootPath))
        {
            var id = Path.GetFileName(dir);
            try
            {
                var s = Load(id);
                if (s is not null) list.Add(s);
            }
            catch (JsonException)
            {
                // A partially written or hand-edited state file must not take down the whole listing.
            }
        }
        return list.OrderByDescending(s => s.CreatedAt).ToList();
    }

    public static string NewRunId(string scenario)
        => $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{scenario}-{Guid.NewGuid().ToString("N")[..6]}";
}
