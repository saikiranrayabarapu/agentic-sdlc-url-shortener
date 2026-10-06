using System.Text.Json;

namespace AgenticSdlc.Core.Llm;

/// <summary>A single model call made by a stage agent. Scenario/node/attempt identify the call for replay and recording.</summary>
public sealed class LlmRequest
{
    public required string Scenario { get; init; }
    public required string NodeId { get; init; }
    public required int Attempt { get; init; }
    public required string SystemPrompt { get; init; }
    public required string UserPrompt { get; init; }
    /// <summary>Name of the JSON contract the agent expects back (for logging and validation).</summary>
    public required string ExpectedSchema { get; init; }
}

public sealed class LlmResponse
{
    public required string Text { get; init; }
    public required string Provider { get; init; }
    public string? Model { get; init; }
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
    public double LatencyMs { get; init; }
    public bool FromReplay { get; init; }

    /// <summary>Extracts the first JSON object from the response, tolerating markdown fences and prose around it.</summary>
    public JsonDocument ParseJson()
    {
        var text = Text.Trim();
        var fenceStart = text.IndexOf("```", StringComparison.Ordinal);
        if (fenceStart >= 0)
        {
            var afterFence = text.IndexOf('\n', fenceStart);
            var fenceEnd = text.LastIndexOf("```", StringComparison.Ordinal);
            if (afterFence > 0 && fenceEnd > afterFence) text = text[(afterFence + 1)..fenceEnd].Trim();
        }
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) throw new LlmContractException("Response did not contain a JSON object.", Text);
        try
        {
            return JsonDocument.Parse(text[start..(end + 1)]);
        }
        catch (JsonException ex)
        {
            throw new LlmContractException($"Response JSON is malformed: {ex.Message}", Text);
        }
    }
}

public sealed class LlmContractException : Exception
{
    public string RawResponse { get; }
    public LlmContractException(string message, string raw) : base(message) => RawResponse = raw;
}

public sealed class LlmUnavailableException : Exception
{
    public LlmUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
}

public interface ILlmClient
{
    string Name { get; }
    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct);
}
