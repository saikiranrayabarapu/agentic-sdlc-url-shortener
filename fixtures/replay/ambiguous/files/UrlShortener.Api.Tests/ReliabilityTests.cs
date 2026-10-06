using System.Net;
using System.Net.Http.Json;
using UrlShortener.Api.Contracts;
using UrlShortener.Api.Infrastructure;

namespace UrlShortener.Api.Tests;

public sealed class ReliabilityTests : IClassFixture<ApiFactory>
{
    private readonly HttpClient _client;

    public ReliabilityTests(ApiFactory factory)
    {
        _client = factory.CreateClient(new() { AllowAutoRedirect = false });
    }

    [Fact]
    public async Task Readiness_probe_reports_database_connectivity()
    {
        var response = await _client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Correlation_id_is_echoed_when_supplied()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/health");
        req.Headers.Add(CorrelationIdMiddleware.HeaderName, "test-corr-123");

        var response = await _client.SendAsync(req);

        Assert.Equal("test-corr-123", response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single());
    }

    [Fact]
    public async Task Correlation_id_is_generated_when_missing()
    {
        var response = await _client.GetAsync("/health");
        var value = response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single();
        Assert.False(string.IsNullOrWhiteSpace(value));
    }

    [Fact]
    public async Task Validation_failures_use_problem_details()
    {
        var response = await _client.PostAsJsonAsync("/api/links", new CreateLinkRequest("nope"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }
}

/// <summary>Uses its own factory with a tiny limit so the rate limiter can be observed deterministically.</summary>
public sealed class RateLimitTests : IDisposable
{
    private readonly ApiFactory _factory = new(new Dictionary<string, string?> { ["RateLimiting:CreatePerMinute"] = "3" });

    [Fact]
    public async Task Create_is_rate_limited_per_fixed_window()
    {
        var client = _factory.CreateClient();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            var response = await client.PostAsJsonAsync("/api/links", new CreateLinkRequest($"https://example.com/{i}"));
            statuses.Add(response.StatusCode);
        }

        Assert.Equal(3, statuses.Count(s => s == HttpStatusCode.Created));
        Assert.Equal(2, statuses.Count(s => s == HttpStatusCode.TooManyRequests));

        // Redirects are not rate limited by the create policy.
        var redirect = await client.GetAsync("/unknown-code");
        Assert.Equal(HttpStatusCode.NotFound, redirect.StatusCode);
    }

    public void Dispose() => _factory.Dispose();
}
