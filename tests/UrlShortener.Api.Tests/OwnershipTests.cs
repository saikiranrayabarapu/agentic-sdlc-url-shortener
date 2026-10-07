using System.Net;
using System.Net.Http.Json;
using UrlShortener.Api.Contracts;
using UrlShortener.Api.Domain;

namespace UrlShortener.Api.Tests;

/// <summary>Anonymous ownership: the manage token issued at creation is the only thing that authorises deleting a link.</summary>
public sealed class OwnershipTests : IClassFixture<ApiFactory>
{
    private readonly HttpClient _client;

    public OwnershipTests(ApiFactory factory)
    {
        _client = factory.CreateClient(new() { AllowAutoRedirect = false });
    }

    [Fact]
    public async Task Create_returns_a_manage_token_once_and_never_again()
    {
        var created = await Create("https://example.com/owned");

        Assert.False(string.IsNullOrWhiteSpace(created.ManageToken));
        Assert.True(created.ManageToken.Length >= 32, "token should carry at least 192 bits of entropy");

        var metadata = await _client.GetStringAsync($"/api/links/{created.Code}");
        Assert.DoesNotContain("manageToken", metadata, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(created.ManageToken, metadata);
    }

    [Fact]
    public async Task Delete_without_a_token_is_forbidden_and_leaves_the_link_intact()
    {
        var created = await Create("https://example.com/keep-me");

        var delete = await _client.DeleteAsync($"/api/links/{created.Code}");

        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
        Assert.Contains("application/problem+json", delete.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.Found, (await _client.GetAsync("/" + created.Code)).StatusCode);
    }

    [Fact]
    public async Task Delete_with_a_wrong_token_is_forbidden()
    {
        var created = await Create("https://example.com/keep-me-too");
        var other = await Create("https://example.com/somebody-else");

        var delete = await Delete(created.Code, other.ManageToken);

        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync($"/api/links/{created.Code}")).StatusCode);
    }

    [Fact]
    public async Task Delete_with_the_right_token_succeeds_once()
    {
        var created = await Create("https://example.com/delete-me");

        var delete = await Delete(created.Code, created.ManageToken);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/links/{created.Code}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Delete(created.Code, created.ManageToken)).StatusCode);
    }

    [Fact]
    public async Task Unknown_code_is_404_regardless_of_token()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Delete("no-such-code", "whatever")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync("/api/links/no-such-code")).StatusCode);
    }

    private async Task<CreatedLinkResponse> Create(string url)
    {
        var response = await _client.PostAsJsonAsync("/api/links", new CreateLinkRequest(url));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CreatedLinkResponse>())!;
    }

    private async Task<HttpResponseMessage> Delete(string code, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/links/{code}");
        request.Headers.Add(ManageToken.HeaderName, token);
        return await _client.SendAsync(request);
    }
}

/// <summary>The create limit is a budget per client address, not one shared bucket for the whole service.</summary>
public sealed class PerClientRateLimitTests : IDisposable
{
    private readonly ApiFactory _factory = new(new Dictionary<string, string?> { ["RateLimiting:CreatePerMinute"] = "2" });

    [Fact]
    public async Task One_client_exhausting_its_budget_does_not_block_another()
    {
        var alice = ClientFrom("203.0.113.10");
        var bob = ClientFrom("203.0.113.11");

        var aliceStatuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
            aliceStatuses.Add((await alice.PostAsJsonAsync("/api/links", new CreateLinkRequest($"https://example.com/alice/{i}"))).StatusCode);

        Assert.Equal(new[] { HttpStatusCode.Created, HttpStatusCode.Created, HttpStatusCode.TooManyRequests }, aliceStatuses);

        var bobResponse = await bob.PostAsJsonAsync("/api/links", new CreateLinkRequest("https://example.com/bob"));
        Assert.Equal(HttpStatusCode.Created, bobResponse.StatusCode);
    }

    private HttpClient ClientFrom(string address)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestClientIpStartupFilter.HeaderName, address);
        return client;
    }

    public void Dispose() => _factory.Dispose();
}
