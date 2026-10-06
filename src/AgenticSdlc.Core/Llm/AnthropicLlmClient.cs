using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;

namespace AgenticSdlc.Core.Llm;

/// <summary>
/// Live client using the official Anthropic SDK. Requires ANTHROPIC_API_KEY.
/// Agents ask for strict JSON in the system prompt; the orchestrator validates the contract and retries on violations.
/// </summary>
public sealed class AnthropicLlmClient : ILlmClient
{
    private readonly AnthropicClient _client;
    private readonly string _model;

    public AnthropicLlmClient(string? apiKey = null, string model = "claude-opus-5-5")
    {
        var key = apiKey ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
            throw new LlmUnavailableException("ANTHROPIC_API_KEY is not set. Use --mode replay or --mode claude-cli instead.");
        _client = new AnthropicClient { ApiKey = key };
        _model = model;
    }

    public string Name => "anthropic";

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        try
        {
            var response = await _client.Messages.Create(new MessageCreateParams
            {
                Model = _model,
                MaxTokens = 16000,
                System = request.SystemPrompt,
                Messages = [new() { Role = Role.User, Content = request.UserPrompt }]
            }, cancellationToken: ct);

            if (response.StopReason?.ToString() == "refusal")
                throw new LlmContractException("Model declined the request (stop_reason=refusal).", string.Empty);

            var text = string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
            return new LlmResponse
            {
                Text = text,
                Provider = Name,
                Model = _model,
                InputTokens = (int?)response.Usage.InputTokens,
                OutputTokens = (int?)response.Usage.OutputTokens,
                LatencyMs = (DateTime.UtcNow - started).TotalMilliseconds
            };
        }
        catch (AnthropicRateLimitException ex)
        {
            throw new LlmUnavailableException("Anthropic rate limit hit; the orchestrator will retry or fall back.", ex);
        }
        catch (AnthropicApiException ex)
        {
            throw new LlmUnavailableException($"Anthropic API error: {ex.Message}", ex);
        }
    }
}
