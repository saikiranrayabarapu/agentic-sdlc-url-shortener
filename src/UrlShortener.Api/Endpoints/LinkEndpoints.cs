using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using UrlShortener.Api.Analytics;
using UrlShortener.Api.Contracts;
using UrlShortener.Api.Data;
using UrlShortener.Api.Domain;

namespace UrlShortener.Api.Endpoints;

public static class RateLimitPolicies
{
    public const string CreateLink = "create-link";
}

public static class LinkEndpoints
{
    private const int MaxCodeCollisionRetries = 5;

    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "ok", utc = DateTimeOffset.UtcNow }))
           .WithName("Liveness");

        app.MapGet("/health/ready", async (AppDbContext db, CancellationToken ct) =>
                await db.Database.CanConnectAsync(ct)
                    ? Results.Ok(new { status = "ready" })
                    : Results.Problem("Database is not reachable.", statusCode: StatusCodes.Status503ServiceUnavailable))
           .WithName("Readiness");
        return app;
    }

    public static IEndpointRouteBuilder MapLinkEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/links").WithTags("Links");

        group.MapPost("/", CreateAsync).RequireRateLimiting(RateLimitPolicies.CreateLink).WithName("CreateLink");
        group.MapGet("/{code}", GetAsync).WithName("GetLink");
        group.MapDelete("/{code}", DeleteAsync).WithName("DeleteLink");
        group.MapGet("/{code}/stats", GetStatsAsync).WithName("GetLinkStats");

        app.MapGet("/{code}", RedirectAsync).WithName("Redirect");
        return app;
    }

    private static async Task<IResult> CreateAsync(
        CreateLinkRequest request, AppDbContext db, ShortCodeGenerator generator,
        IOptions<UrlValidationOptions> validation, HttpContext http, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (!UrlValidator.TryValidateUrl(request.Url, validation.Value, out var uri, out var urlError)) errors["url"] = new[] { urlError! };
        if (!UrlValidator.TryValidateAlias(request.CustomAlias, out var aliasError)) errors["customAlias"] = new[] { aliasError! };
        if (request.ExpiresAt is { } exp && exp <= DateTimeOffset.UtcNow) errors["expiresAt"] = new[] { "expiresAt must be in the future." };
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        if (request.CustomAlias is not null)
        {
            if (await db.Links.AnyAsync(l => l.Code == request.CustomAlias, ct))
                return Results.Problem($"Alias '{request.CustomAlias}' is already taken.", statusCode: StatusCodes.Status409Conflict);

            var custom = new ShortLink { Code = request.CustomAlias, OriginalUrl = uri!.ToString(), IsCustomAlias = true, ExpiresAt = request.ExpiresAt };
            db.Links.Add(custom);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                return Results.Problem($"Alias '{request.CustomAlias}' is already taken.", statusCode: StatusCodes.Status409Conflict);
            }
            return Results.Created($"/api/links/{custom.Code}", ToResponse(custom, http));
        }

        // Random code with bounded collision retry. Unique index is the source of truth.
        for (var attempt = 0; attempt < MaxCodeCollisionRetries; attempt++)
        {
            var link = new ShortLink { Code = generator.Generate(), OriginalUrl = uri!.ToString(), ExpiresAt = request.ExpiresAt };
            db.Links.Add(link);
            try
            {
                await db.SaveChangesAsync(ct);
                return Results.Created($"/api/links/{link.Code}", ToResponse(link, http));
            }
            catch (DbUpdateException)
            {
                db.Entry(link).State = EntityState.Detached;
            }
        }
        return Results.Problem("Could not allocate a unique short code; please retry.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task<IResult> GetAsync(string code, AppDbContext db, HttpContext http, CancellationToken ct)
    {
        var link = await db.Links.AsNoTracking().FirstOrDefaultAsync(l => l.Code == code, ct);
        return link is null ? Results.NotFound() : Results.Ok(ToResponse(link, http));
    }

    private static async Task<IResult> DeleteAsync(string code, AppDbContext db, CancellationToken ct)
    {
        var deleted = await db.Links.Where(l => l.Code == code).ExecuteDeleteAsync(ct);
        return deleted == 0 ? Results.NotFound() : Results.NoContent();
    }

    private static async Task<IResult> RedirectAsync(string code, AppDbContext db, ClickQueue clicks, HttpContext http, CancellationToken ct)
    {
        var link = await db.Links.AsNoTracking().FirstOrDefaultAsync(l => l.Code == code, ct);
        if (link is null) return Results.NotFound();
        if (link.IsExpired(DateTimeOffset.UtcNow))
            return Results.Problem("This link has expired.", statusCode: StatusCodes.Status410Gone);

        // Fire-and-forget into a bounded in-memory queue: analytics never add latency to the redirect path.
        clicks.TryEnqueue(new ClickEvent
        {
            LinkId = link.Id,
            OccurredAtUtc = DateTime.UtcNow,
            Referrer = Truncate(http.Request.Headers.Referer.ToString(), 2048),
            UserAgent = Truncate(http.Request.Headers.UserAgent.ToString(), 512)
        });

        return Results.Redirect(link.OriginalUrl, permanent: false);
    }

    private static async Task<IResult> GetStatsAsync(string code, AppDbContext db, CancellationToken ct)
    {
        var link = await db.Links.AsNoTracking().FirstOrDefaultAsync(l => l.Code == code, ct);
        if (link is null) return Results.NotFound();

        var windowStart = DateTime.UtcNow.Date.AddDays(-6);

        var clicks = db.Clicks.AsNoTracking().Where(c => c.LinkId == link.Id);
        var total = await clicks.LongCountAsync(ct);
        DateTimeOffset? last = total == 0
            ? null
            : new DateTimeOffset(DateTime.SpecifyKind(await clicks.MaxAsync(c => c.OccurredAtUtc, ct), DateTimeKind.Utc));

        var recent = await clicks.Where(c => c.OccurredAtUtc >= windowStart)
            .Select(c => new { c.OccurredAtUtc, c.Referrer })
            .ToListAsync(ct);

        var perDay = Enumerable.Range(0, 7)
            .Select(i => DateOnly.FromDateTime(windowStart.AddDays(i)))
            .Select(day => new DailyClicks(day, recent.Count(c => DateOnly.FromDateTime(c.OccurredAtUtc) == day)))
            .ToList();

        var topReferrers = recent
            .GroupBy(c => string.IsNullOrWhiteSpace(c.Referrer) ? "(direct)" : c.Referrer!)
            .Select(g => new ReferrerClicks(g.Key, g.Count()))
            .OrderByDescending(r => r.Clicks).ThenBy(r => r.Referrer)
            .Take(5)
            .ToList();

        return Results.Ok(new LinkStatsResponse(link.Code, total, last, perDay, topReferrers));
    }

    private static LinkResponse ToResponse(ShortLink link, HttpContext http)
    {
        var shortUrl = $"{http.Request.Scheme}://{http.Request.Host}/{link.Code}";
        return new LinkResponse(link.Code, shortUrl, link.OriginalUrl, link.IsCustomAlias, link.CreatedAt, link.ExpiresAt);
    }

    private static string? Truncate(string? value, int max)
        => string.IsNullOrEmpty(value) ? null : value.Length <= max ? value : value[..max];
}
