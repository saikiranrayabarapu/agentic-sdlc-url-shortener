using System.Net;
using System.Net.Http.Json;
using UrlShortener.Api.Contracts;
using UrlShortener.Api.Domain;

namespace UrlShortener.Api.Tests;

public sealed class LinksApiTests : IClassFixture<ApiFactory>
{
    private readonly HttpClient _client;

    public LinksApiTests(ApiFactory factory)
    {
        _client = factory.CreateClient(new() { AllowAutoRedirect = false });
    }

    [Fact]
    public async Task Create_returns_201_with_seven_char_code_and_short_url()
    {
        var response = await _client.PostAsJsonAsync("/api/links", new CreateLinkRequest("https://example.com/some/long/path?x=1"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<LinkResponse>();
        Assert.NotNull(body);
        Assert.Equal(7, body!.Code.Length);
        Assert.EndsWith("/" + body.Code, body.ShortUrl);
        Assert.Equal("https://example.com/some/long/path?x=1", body.OriginalUrl);
        Assert.Equal($"/api/links/{body.Code}", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Redirect_returns_302_to_original_url()
    {
        var created = await Create("https://example.com/redirect-target");

        var response = await _client.GetAsync("/" + created.Code);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("https://example.com/redirect-target", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Unknown_code_returns_404()
    {
        var response = await _client.GetAsync("/doesnotexist");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("ftp://example.com/file")]
    [InlineData("javascript:alert(1)")]
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData("http://localhost/internal")]
    [InlineData("http://10.0.0.5/admin")]
    [InlineData("https://user:pass@example.com/")]
    public async Task Invalid_urls_are_rejected_with_400(string url)
    {
        var response = await _client.PostAsJsonAsync("/api/links", new CreateLinkRequest(url));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Custom_alias_is_honoured_and_duplicate_alias_conflicts()
    {
        var alias = "my-" + Guid.NewGuid().ToString("N")[..8];

        var first = await _client.PostAsJsonAsync("/api/links", new CreateLinkRequest("https://example.com/a", alias));
        var second = await _client.PostAsJsonAsync("/api/links", new CreateLinkRequest("https://example.com/b", alias));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var body = await first.Content.ReadFromJsonAsync<LinkResponse>();
        Assert.Equal(alias, body!.Code);
        Assert.True(body.IsCustomAlias);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("has space")]
    [InlineData("api")]
    [InlineData("way-too-long-alias-way-too-long-alias-way-too-long")]
    public async Task Invalid_aliases_are_rejected(string alias)
    {
        var response = await _client.PostAsJsonAsync("/api/links", new CreateLinkRequest("https://example.com/", alias));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Expired_link_returns_410()
    {
        // A generous expiry keeps the create request valid under load; we then poll until the link has expired.
        var created = await Create("https://example.com/expiring", expiresAt: DateTimeOffset.UtcNow.AddSeconds(1.5));

        var deadline = DateTime.UtcNow.AddSeconds(8);
        HttpStatusCode status;
        do
        {
            await Task.Delay(250);
            status = (await _client.GetAsync("/" + created.Code)).StatusCode;
        } while (status != HttpStatusCode.Gone && DateTime.UtcNow < deadline);

        Assert.Equal(HttpStatusCode.Gone, status);
    }

    [Fact]
    public async Task Expiry_in_the_past_is_rejected()
    {
        var response = await _client.PostAsJsonAsync("/api/links", new CreateLinkRequest("https://example.com/", null, DateTimeOffset.UtcNow.AddMinutes(-1)));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Get_metadata_and_delete()
    {
        var create = await _client.PostAsJsonAsync("/api/links", new CreateLinkRequest("https://example.com/meta"));
        var created = (await create.Content.ReadFromJsonAsync<CreatedLinkResponse>())!;

        var get = await _client.GetAsync($"/api/links/{created.Code}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);

        // Deleting needs the manage token issued at creation (ownership is covered in depth by OwnershipTests).
        var delete = await DeleteWithToken(created.Code, created.ManageToken);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var afterDelete = await _client.GetAsync($"/api/links/{created.Code}");
        Assert.Equal(HttpStatusCode.NotFound, afterDelete.StatusCode);

        var deleteAgain = await DeleteWithToken(created.Code, created.ManageToken);
        Assert.Equal(HttpStatusCode.NotFound, deleteAgain.StatusCode);
    }

    private async Task<HttpResponseMessage> DeleteWithToken(string code, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/links/{code}");
        request.Headers.Add(ManageToken.HeaderName, token);
        return await _client.SendAsync(request);
    }

    [Fact]
    public async Task Health_endpoint_is_live()
    {
        var response = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<LinkResponse> Create(string url, string? alias = null, DateTimeOffset? expiresAt = null)
    {
        var response = await _client.PostAsJsonAsync("/api/links", new CreateLinkRequest(url, alias, expiresAt));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LinkResponse>())!;
    }
}
