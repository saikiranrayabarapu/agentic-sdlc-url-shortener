using System.Text;
using System.Text.Json;
using AgenticSdlc.Core.Llm;
using AgenticSdlc.Core.Model;
using AgenticSdlc.Core.Workspace;

namespace AgenticSdlc.Core.Agents;

public enum StageOutcome
{
    Succeeded,
    Failed,
    /// <summary>The agent found an upstream artifact it cannot work with and asks the orchestrator to re-run that stage.</summary>
    ChangeRequest,
    /// <summary>The requirement is too ambiguous to proceed safely; a human must answer before planning continues.</summary>
    ClarificationNeeded
}

public sealed class StageResult
{
    public required StageOutcome Outcome { get; init; }
    public string ArtifactType { get; init; } = "Artifact";
    public string Summary { get; init; } = "";
    public JsonElement? Content { get; init; }
    public ChangeSet? ChangeSet { get; init; }
    public ImpactLevel Impact { get; init; } = ImpactLevel.Low;
    public string? Error { get; init; }
    /// <summary>Guidance for the next attempt (compiler output, failing tests, contract violation).</summary>
    public string? Feedback { get; init; }
    public string? ChangeRequestTargetNodeId { get; init; }
    public string? ChangeRequestReason { get; init; }
    public List<ClarificationQuestion> Questions { get; init; } = new();

    public static StageResult Fail(string error, string? feedback = null) => new()
    {
        Outcome = StageOutcome.Failed, Error = error, Feedback = feedback ?? error, Summary = error
    };
}

/// <summary>Everything an agent may read. Agents never touch the run state directly; the orchestrator owns all transitions.</summary>
public sealed class StageContext
{
    public required RunState Run { get; init; }
    public required StageNode Node { get; init; }
    public required int Attempt { get; init; }
    public required Workspace.Workspace Workspace { get; init; }
    public required ILlmClient Llm { get; init; }
    public required Action<string, string, Dictionary<string, object?>?> Audit { get; init; }
    public string? Feedback { get; init; }

    public Artifact? Upstream(string nodeId) => Run.Workflow.Has(nodeId) ? Run.LatestArtifact(nodeId) : null;

    public Artifact? UpstreamOfKind(StageKind kind)
    {
        var node = Run.Workflow.Nodes.FirstOrDefault(n => n.Kind == kind);
        return node is null ? null : Run.LatestArtifact(node.Id);
    }

    public string UpstreamJson(StageKind kind, int maxChars = 12000)
    {
        var a = UpstreamOfKind(kind);
        if (a is null) return "(not available)";
        var json = a.Content.GetRawText();
        return json.Length > maxChars ? json[..maxChars] + "\n/* truncated */" : json;
    }
}

public interface IStageAgent
{
    StageKind Kind { get; }
    Task<StageResult> ExecuteAsync(StageContext ctx, CancellationToken ct);
}

/// <summary>Shared plumbing for model-backed agents: prompt assembly, the model call, contract parsing, audit of the call.</summary>
public abstract class LlmStageAgent : IStageAgent
{
    public abstract StageKind Kind { get; }
    protected abstract string Role { get; }
    protected abstract string Contract { get; }
    protected abstract string BuildUserPrompt(StageContext ctx);
    protected abstract StageResult Interpret(StageContext ctx, JsonElement root);

    public async Task<StageResult> ExecuteAsync(StageContext ctx, CancellationToken ct)
    {
        var system = new StringBuilder()
            .AppendLine($"You are the {Role} agent inside a governed software-delivery pipeline for a .NET 10 / C# code base.")
            .AppendLine("Work only from the context provided. Be concrete and production-minded.")
            .AppendLine("Respond with a single JSON object and nothing else. It MUST match this contract exactly:")
            .AppendLine(Contract)
            .ToString();

        var user = new StringBuilder();
        user.AppendLine($"# Scenario: {ctx.Run.Scenario} ({ctx.Run.ScenarioKind})");
        user.AppendLine($"# Requirement\n{ctx.Run.RequirementText}");
        if (ctx.Run.ClarificationAnswers.Count > 0)
        {
            user.AppendLine("# Clarifications decided by the human reviewer");
            foreach (var kv in ctx.Run.ClarificationAnswers) user.AppendLine($"- {kv.Key}: {kv.Value}");
        }
        if (!string.IsNullOrWhiteSpace(ctx.Feedback))
        {
            user.AppendLine("# Feedback from the previous attempt (you MUST address this)");
            user.AppendLine(ctx.Feedback);
        }
        user.AppendLine(BuildUserPrompt(ctx));

        var request = new LlmRequest
        {
            Scenario = ctx.Run.Scenario,
            NodeId = ctx.Node.Id,
            Attempt = ctx.Attempt,
            SystemPrompt = system,
            UserPrompt = user.ToString(),
            ExpectedSchema = GetType().Name.Replace("Agent", "") + "Contract"
        };

        var response = await ctx.Llm.CompleteAsync(request, ct);
        ctx.Audit("LlmCall", $"{ctx.Node.Id} called model via {response.Provider}", new()
        {
            ["provider"] = response.Provider,
            ["model"] = response.Model,
            ["latencyMs"] = Math.Round(response.LatencyMs, 1),
            ["inputTokens"] = response.InputTokens,
            ["outputTokens"] = response.OutputTokens,
            ["replay"] = response.FromReplay,
            ["promptChars"] = request.UserPrompt.Length
        });

        try
        {
            using var doc = response.ParseJson();
            return Interpret(ctx, doc.RootElement.Clone());
        }
        catch (LlmContractException ex)
        {
            return StageResult.Fail($"Model output violated the {request.ExpectedSchema}: {ex.Message}",
                $"Your previous response was not valid JSON for the contract. Error: {ex.Message}. Return only the JSON object.");
        }
    }

    protected static ChangeSet ParseChangeSet(JsonElement root)
    {
        var files = new List<FileChange>();
        if (root.TryGetProperty("files", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in arr.EnumerateArray())
            {
                files.Add(new FileChange
                {
                    Path = f.GetProperty("path").GetString()!,
                    Action = f.TryGetProperty("action", out var a) ? a.GetString() ?? "create" : "create",
                    Content = f.TryGetProperty("content", out var c) ? c.GetString() : null,
                    Reason = f.TryGetProperty("reason", out var r) ? r.GetString() : null
                });
            }
        }
        return new ChangeSet
        {
            Summary = root.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "",
            Impact = root.TryGetProperty("impact", out var i) ? i.GetString() ?? "Low" : "Low",
            Files = files,
            Risks = Strings(root, "risks"),
            Assumptions = Strings(root, "assumptions")
        };
    }

    protected static List<string> Strings(JsonElement root, string prop)
    {
        var list = new List<string>();
        if (root.TryGetProperty(prop, out var arr) && arr.ValueKind == JsonValueKind.Array)
            list.AddRange(arr.EnumerateArray().Select(e => e.ToString()));
        return list;
    }

    protected static string Str(JsonElement root, string prop, string fallback = "")
        => root.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;

    protected static string WorkspaceListing(StageContext ctx, int max = 80)
    {
        var files = ctx.Workspace.ListFiles();
        if (files.Count == 0) return "(workspace is empty)";
        var shown = files.Take(max).Select(f => "- " + f);
        var more = files.Count > max ? $"\n... and {files.Count - max} more" : "";
        return string.Join('\n', shown) + more;
    }
}
