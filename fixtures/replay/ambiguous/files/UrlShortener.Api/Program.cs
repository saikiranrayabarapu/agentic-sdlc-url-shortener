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
    options.AddFixedWindowLimiter(RateLimitPolicies.CreateLink, limiter =>
    {
        limiter.PermitLimit = builder.Configuration.GetValue("RateLimiting:CreatePerMinute", 30);
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
        limiter.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
    });
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    // EnsureCreated keeps local setup to zero steps. Production deployments should switch to EF migrations.
    scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseRateLimiter();
app.MapOpenApi();

app.MapHealthEndpoints();
app.MapLinkEndpoints();

app.Run();

/// <summary>Exposed for integration tests (WebApplicationFactory).</summary>
public partial class Program { }
