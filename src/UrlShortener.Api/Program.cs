using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using UrlShortener.Api.Analytics;
using UrlShortener.Api.Data;
using UrlShortener.Api.Domain;
using UrlShortener.Api.Endpoints;
using UrlShortener.Api.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// --- Persistence -------------------------------------------------------------------------------
var connectionString = builder.Configuration.GetConnectionString("Links") ?? "Data Source=urlshortener.db";
builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite(connectionString));

// --- Domain services ---------------------------------------------------------------------------
builder.Services.AddSingleton<ShortCodeGenerator>();
builder.Services.Configure<UrlValidationOptions>(builder.Configuration.GetSection("UrlValidation"));

// --- Analytics: redirects enqueue, a background writer persists in batches --------------------
builder.Services.AddSingleton<ClickQueue>();
builder.Services.AddHostedService<ClickRecorder>();

// --- Reliability: problem details, rate limiting, OpenAPI -------------------------------------
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // One fixed window per client address, so a single abusive caller cannot exhaust the budget for everyone.
    // Behind a reverse proxy, enable ForwardedHeaders so RemoteIpAddress is the real client and not the proxy.
    var createPerMinute = builder.Configuration.GetValue("RateLimiting:CreatePerMinute", 30);
    options.AddPolicy(RateLimitPolicies.CreateLink, http =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = createPerMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
            }));
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    // EnsureCreated keeps local setup to zero steps. Production deployments should switch to EF migrations.
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
    // EnsureCreated never alters an existing database, so columns added in later versions are patched in here.
    SchemaUpgrades.Apply(db);
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseMiddleware<CorrelationIdMiddleware>();

// --- Web UI: static files from wwwroot. The redirect route is constrained to the short-code alphabet, so
//     asset paths (/app.js, /styles.css, /favicon.ico) never match it and are served from disk instead.
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();
app.MapOpenApi();

app.MapHealthEndpoints();
app.MapLinkEndpoints();

app.Run();

/// <summary>Exposed for integration tests (WebApplicationFactory).</summary>
public partial class Program { }
