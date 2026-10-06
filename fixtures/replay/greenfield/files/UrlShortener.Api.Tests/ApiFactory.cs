using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace UrlShortener.Api.Tests;

/// <summary>
/// Boots the real application against an isolated SQLite file per factory instance, so tests are hermetic and
/// run in parallel without sharing state. Settings can be overridden per test class (e.g. a tiny rate limit).
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"urlshortener-tests-{Guid.NewGuid():N}.db");
    private readonly Dictionary<string, string?> _settings;

    public ApiFactory() : this(new Dictionary<string, string?>()) { }

    // Internal so xUnit's class-fixture rule (exactly one public constructor) still holds.
    internal ApiFactory(Dictionary<string, string?> settings) => _settings = settings;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Links", $"Data Source={_dbPath}");
        builder.UseSetting("RateLimiting:CreatePerMinute", "1000");
        builder.UseSetting("UrlValidation:BlockPrivateHosts", "true");
        foreach (var kv in _settings) builder.UseSetting(kv.Key, kv.Value);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try
        {
            if (File.Exists(_dbPath)) File.Delete(_dbPath);
        }
        catch (IOException)
        {
            // SQLite may still hold the handle for a moment; a leftover temp file is harmless.
        }
    }
}
