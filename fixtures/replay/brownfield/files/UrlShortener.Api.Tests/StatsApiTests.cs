using System.Net;
using System.Net.Http.Json;
using UrlShortener.Api.Contracts;

namespace UrlShortener.Api.Tests;

public sealed class StatsApiTests : IClassFixture<ApiFactory>
{
    private readonly HttpClient _client;

    public StatsApiTests(ApiFactory factory)
    {
        _client = factory.CreateClient(new() { AllowAutoRedirect = false });
    }

    [Fact]
    public async Task Clicks_are_recorded_asynchronously_and_aggregated()
    {
        var created = await Create("https://example.com/analytics");

        for (var i = 0; i < 3; i++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/" + created.Code);
            req.Headers.Referrer = new Uri("https://news.example.org/post/1");
            req.Headers.UserAgent.ParseAdd("StatsTest/1.0");
            var redirect = await _client.SendAsync(req);
            Assert.Equal(HttpStatusCode.Found, redirect.StatusCode);
        }
        var direct = await _client.GetAsync("/" + created.Code);
        Assert.Equal(HttpStatusCode.Found, direct.StatusCode);

        var stats = await WaitForClicks(created.Code, expected: 4);

        Assert.Equal(4, stats.TotalClicks);
        Assert.NotNull(stats.LastClickedAt);
        Assert.Equal(7, stats.ClicksPerDay.Count);
        Assert.Equal(4, stats.ClicksPerDay.Sum(d => d.Clicks));
        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow), stats.ClicksPerDay[^1].Date);
        Assert.Equal("https://news.example.org/post/1", stats.TopReferrers[0].Referrer);
        Assert.Equal(3, stats.TopReferrers[0].Clicks);
        Assert.Contains(stats.TopReferrers, r => r.Referrer == "(direct)" && r.Clicks == 1);
    }

    [Fact]
    public async Task Stats_for_unclicked_link_are_zero()
    {
        var created = await Create("https://example.com/quiet");

        var stats = await _client.GetFromJsonAsync<LinkStatsResponse>($"/api/links/{created.Code}/stats");

        Assert.NotNull(stats);
        Assert.Equal(0, stats!.TotalClicks);
        Assert.Null(stats.LastClickedAt);
        Assert.Empty(stats.TopReferrers);
    }

    [Fact]
    public async Task Stats_for_unknown_link_returns_404()
    {
        var response = await _client.GetAsync("/api/links/nope123/stats");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Redirect_latency_does_not_wait_for_analytics_write()
    {
        var created = await Create("https://example.com/fast");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 20; i++) Assert.Equal(HttpStatusCode.Found, (await _client.GetAsync("/" + created.Code)).StatusCode);
        sw.Stop();
        // Generous bound: 20 redirects through the in-process test server should be far below this even on CI.
        Assert.True(sw.ElapsedMilliseconds < 5000, $"20 redirects took {sw.ElapsedMilliseconds}ms");
        var stats = await WaitForClicks(created.Code, expected: 20);
        Assert.Equal(20, stats.TotalClicks);
    }

    private async Task<LinkStatsResponse> WaitForClicks(string code, int expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        LinkStatsResponse? stats = null;
        while (DateTime.UtcNow < deadline)
        {
            stats = await _client.GetFromJsonAsync<LinkStatsResponse>($"/api/links/{code}/stats");
            if (stats is not null && stats.TotalClicks >= expected) return stats;
            await Task.Delay(100);
        }
        Assert.Fail($"Expected {expected} clicks, saw {stats?.TotalClicks}");
        return stats!;
    }

    private async Task<LinkResponse> Create(string url)
    {
        var response = await _client.PostAsJsonAsync("/api/links", new CreateLinkRequest(url));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LinkResponse>())!;
    }
}
