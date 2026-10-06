using System.Text.Json;
using System.Text.Json.Nodes;
using AgenticSdlc.Core.Llm;
using AgenticSdlc.Core.Workspace;

namespace AgenticSdlc.Core.Engine;

/// <summary>
/// Seeds a run's workspace from the recorded outputs of earlier scenarios, so brownfield runs start from the
/// code the greenfield run produced (and the ambiguous run starts from greenfield + brownfield).
/// Only the final (highest-attempt, non-change-request) recording of each change-set stage is applied.
/// </summary>
public static class WorkspaceSeeder
{
    private static readonly string[] ChangeSetNodes = { "implementation", "tests", "docs" };

    public static IReadOnlyList<string> Seed(Workspace.Workspace workspace, string fixturesRoot, IEnumerable<string> seedScenarios)
    {
        var touched = new List<string>();
        foreach (var scenario in seedScenarios)
        {
            var dir = Path.Combine(fixturesRoot, scenario);
            if (!Directory.Exists(dir)) throw new DirectoryNotFoundException($"Seed fixtures for '{scenario}' not found at {dir}");
            foreach (var nodeId in ChangeSetNodes)
            {
                var path = FinalFixture(dir, nodeId);
                if (path is null) continue;
                var node = ReplayLlmClient.LoadFixture(path);
                var json = JsonDocument.Parse(node.ToJsonString()).RootElement;
                var changeSet = ToChangeSet(json);
                touched.AddRange(workspace.Apply(changeSet).Select(p => $"{scenario}/{nodeId}: {p}"));
            }
        }
        return touched;
    }

    private static string? FinalFixture(string dir, string nodeId)
    {
        var candidates = Directory.GetFiles(dir, $"{nodeId}.*.json").Concat(Directory.GetFiles(dir, $"{nodeId}.json"))
            .Select(p => (Path: p, Attempt: AttemptOf(p)))
            .OrderByDescending(x => x.Attempt)
            .ToList();
        foreach (var c in candidates)
        {
            var node = JsonNode.Parse(File.ReadAllText(c.Path)) as JsonObject;
            if (node is null) continue;
            if (node.ContainsKey("changeRequest")) continue;
            if (node["files"] is JsonArray files && files.Count > 0) return c.Path;
        }
        return null;
    }

    private static int AttemptOf(string path)
    {
        var parts = Path.GetFileNameWithoutExtension(path).Split('.');
        return parts.Length > 1 && int.TryParse(parts[^1], out var n) ? n : 0;
    }

    private static ChangeSet ToChangeSet(JsonElement root)
    {
        var files = new List<FileChange>();
        foreach (var f in root.GetProperty("files").EnumerateArray())
        {
            files.Add(new FileChange
            {
                Path = f.GetProperty("path").GetString()!,
                Action = f.TryGetProperty("action", out var a) ? a.GetString() ?? "create" : "create",
                Content = f.TryGetProperty("content", out var c) ? c.GetString() : null
            });
        }
        return new ChangeSet { Summary = "seed", Files = files };
    }
}
