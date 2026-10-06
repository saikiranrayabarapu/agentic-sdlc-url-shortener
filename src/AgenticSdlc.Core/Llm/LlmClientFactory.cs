using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgenticSdlc.Core.Llm;

/// <summary>Decorator that records live responses as replay fixtures so a live run can later be replayed deterministically.</summary>
public sealed class RecordingLlmClient : ILlmClient
{
    private readonly ILlmClient _inner;
    private readonly string _fixturesRoot;

    public RecordingLlmClient(ILlmClient inner, string fixturesRoot)
    {
        _inner = inner;
        _fixturesRoot = fixturesRoot;
    }

    public string Name => _inner.Name + "+record";

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        var response = await _inner.CompleteAsync(request, ct);
        try
        {
            using var doc = response.ParseJson();
            var dir = Path.Combine(_fixturesRoot, request.Scenario);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"{request.NodeId}.{request.Attempt}.json");
            var node = JsonNode.Parse(doc.RootElement.GetRawText())!;
            File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (LlmContractException)
        {
            // Nothing valid to record; the agent will surface the contract violation.
        }
        return response;
    }
}

/// <summary>
/// Primary/fallback composition: if the live provider is unavailable (no key, rate limit, CLI missing) and a replay
/// fixture exists, fall back to it and let the orchestrator record a Fallback decision.
/// </summary>
public sealed class FallbackLlmClient : ILlmClient
{
    private readonly ILlmClient _primary;
    private readonly ReplayLlmClient _fallback;

    public FallbackLlmClient(ILlmClient primary, ReplayLlmClient fallback)
    {
        _primary = primary;
        _fallback = fallback;
    }

    public string Name => _primary.Name;

    public event Action<LlmRequest, Exception>? FellBack;

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        try
        {
            return await _primary.CompleteAsync(request, ct);
        }
        catch (LlmUnavailableException ex) when (_fallback.HasFixture(request.Scenario, request.NodeId, request.Attempt))
        {
            FellBack?.Invoke(request, ex);
            return await _fallback.CompleteAsync(request, ct);
        }
    }
}

public static class LlmClientFactory
{
    public static readonly string[] Modes = { "replay", "anthropic", "claude-cli" };

    public static ILlmClient Create(string mode, string fixturesRoot, bool record, out FallbackLlmClient? fallbackWrapper)
    {
        fallbackWrapper = null;
        var replay = new ReplayLlmClient(fixturesRoot);
        switch (mode.ToLowerInvariant())
        {
            case "replay":
                return replay;
            case "anthropic":
            {
                ILlmClient live = new AnthropicLlmClient();
                if (record) live = new RecordingLlmClient(live, fixturesRoot);
                fallbackWrapper = new FallbackLlmClient(live, replay);
                return fallbackWrapper;
            }
            case "claude-cli":
            {
                ILlmClient live = new ClaudeCliLlmClient();
                if (record) live = new RecordingLlmClient(live, fixturesRoot);
                fallbackWrapper = new FallbackLlmClient(live, replay);
                return fallbackWrapper;
            }
            default:
                throw new ArgumentException($"Unknown mode '{mode}'. Valid modes: {string.Join(", ", Modes)}");
        }
    }
}
