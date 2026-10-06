using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgenticSdlc.Core.Llm;

/// <summary>
/// Deterministic client that replays recorded agent outputs from <c>fixtures/replay/{scenario}/{nodeId}[.{attempt}].json</c>.
/// Fixtures may reference file contents via <c>"contentFile": "files/..."</c> instead of inlining large code strings;
/// those references are expanded at load time so the agent sees the same JSON contract a live model would return.
/// </summary>
public sealed class ReplayLlmClient : ILlmClient
{
    private readonly string _fixturesRoot;

    public ReplayLlmClient(string fixturesRoot)
    {
        _fixturesRoot = Path.GetFullPath(fixturesRoot);
    }

    public string Name => "replay";

    public string FixturesRoot => _fixturesRoot;

    public bool HasFixture(string scenario, string nodeId, int attempt) => ResolvePath(scenario, nodeId, attempt) is not null;

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var path = ResolvePath(request.Scenario, request.NodeId, request.Attempt)
            ?? throw new LlmUnavailableException(
                $"No replay fixture for scenario '{request.Scenario}', node '{request.NodeId}', attempt {request.Attempt}. " +
                $"Looked in {Path.Combine(_fixturesRoot, request.Scenario)}.");

        var node = LoadFixture(path);

        // Simulate a small amount of model latency so timing metrics are non-trivial and parallelism is observable.
        await Task.Delay(Random.Shared.Next(150, 400), ct);

        var text = node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        return new LlmResponse
        {
            Text = text,
            Provider = Name,
            Model = "recorded",
            FromReplay = true,
            LatencyMs = (DateTime.UtcNow - started).TotalMilliseconds
        };
    }

    /// <summary>Loads a fixture and expands any <c>contentFile</c> references relative to the fixture's folder.</summary>
    public static JsonNode LoadFixture(string path)
    {
        var node = JsonNode.Parse(File.ReadAllText(path)) ?? throw new LlmContractException("Fixture is empty.", path);
        ExpandContentFiles(node, Path.GetDirectoryName(path)!);
        return node;
    }

    /// <summary>
    /// Resolution order: exact attempt file, then the attempt-less file, then the highest recorded earlier attempt
    /// (so a third repair attempt replays the last known response instead of failing for lack of a recording).
    /// </summary>
    private string? ResolvePath(string scenario, string nodeId, int attempt)
    {
        var dir = Path.Combine(_fixturesRoot, scenario);
        if (!Directory.Exists(dir)) return null;

        var exact = Path.Combine(dir, $"{nodeId}.{attempt}.json");
        if (File.Exists(exact)) return exact;

        var generic = Path.Combine(dir, $"{nodeId}.json");
        if (File.Exists(generic)) return generic;

        return Directory.GetFiles(dir, $"{nodeId}.*.json")
            .Select(p => (Path: p, Attempt: AttemptOf(p)))
            .Where(x => x.Attempt > 0 && x.Attempt < attempt)
            .OrderByDescending(x => x.Attempt)
            .Select(x => x.Path)
            .FirstOrDefault();
    }

    private static int AttemptOf(string path)
    {
        var parts = Path.GetFileNameWithoutExtension(path).Split('.');
        return parts.Length > 1 && int.TryParse(parts[^1], out var n) ? n : 0;
    }

    private static void ExpandContentFiles(JsonNode node, string baseDir)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["contentFile"] is JsonValue cf && cf.TryGetValue<string>(out var rel))
                {
                    var full = Path.GetFullPath(Path.Combine(baseDir, rel));
                    if (!full.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"Fixture contentFile escapes fixture directory: {rel}");
                    obj["content"] = File.ReadAllText(full);
                    obj.Remove("contentFile");
                }
                foreach (var kv in obj.ToList())
                {
                    if (kv.Value is not null) ExpandContentFiles(kv.Value, baseDir);
                }
                break;
            case JsonArray arr:
                foreach (var item in arr)
                {
                    if (item is not null) ExpandContentFiles(item, baseDir);
                }
                break;
        }
    }
}
