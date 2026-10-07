using System.Net;
using System.Net.Http.Json;
using UrlShortener.Api.Contracts;

namespace UrlShortener.Api.Tests;

/// <summary>
/// The web UI is static content served by the API itself. These tests pin the contract between the two: the page and
/// its assets are reachable, asset-like paths never fall into the redirect route, and short links keep working.
/// </summary>
public sealed class UiTests : IClassFixture<ApiFactory>
{
    private readonly HttpClient _client;

    public UiTests(ApiFactory factory)
    {
        _client = factory.CreateClient(new() { AllowAutoRedirect = false });
    }

    [Fact]
    public async Task Root_serves_the_shortener_page()
    {
        var response = await _client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Shorten a long link", html);
        Assert.Contains("/app.js", html);
        Assert.Contains("/styles.css", html);
    }

    [Theory]
    [InlineData("/app.js", "javascript")]
    [InlineData("/styles.css", "text/css")]
    public async Task Static_assets_are_served_with_their_content_type(string path, string expectedMediaType)
    {
        var response = await _client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(expectedMediaType, response.Content.Headers.ContentType?.MediaType);
        Assert.NotEmpty(await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/missing.txt")]   // looks like a file: dots are not in the short-code alphabet
    [InlineData("/favicon.ico")]
    [InlineData("/abc")]           // shorter than any code or alias
    public async Task Paths_that_cannot_be_short_codes_return_404_without_a_redirect(string path)
    {
        var response = await _client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task Short_links_still_redirect_alongside_the_static_site()
    {
        var aliased = await Create(new CreateLinkRequest("https://example.com/ui-target", "ui-smoke-test"));
        var generated = await Create(new CreateLinkRequest("https://example.com/ui-target-2"));

        foreach (var link in new[] { aliased, generated })
        {
            var response = await _client.GetAsync("/" + link.Code);
            Assert.Equal(HttpStatusCode.Found, response.StatusCode);
            Assert.Equal(link.OriginalUrl, response.Headers.Location?.ToString());
        }
    }

    private async Task<LinkResponse> Create(CreateLinkRequest request)
    {
        var response = await _client.PostAsJsonAsync("/api/links", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<LinkResponse>())!;
    }
}
