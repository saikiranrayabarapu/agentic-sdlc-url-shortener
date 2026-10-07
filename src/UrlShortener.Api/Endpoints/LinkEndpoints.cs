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

        // Constrained to the short-code alphabet so static assets and junk paths never reach the database.
        app.MapGet("/{code:regex(" + UrlValidator.CodeRoutePattern + ")}", RedirectAsync).WithName("Redirect");
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

        // One secret per link, returned exactly once: the only thing that authorises deleting the link later.
        var token = ManageToken.Generate();
        var tokenHash = ManageToken.Hash(token);

        if (request.CustomAlias is not null)
        {
            if (await db.Links.AnyAsync(l => l.Code == request.CustomAlias, ct))
                return Results.Problem($"Alias '{request.CustomAlias}' is already taken.", statusCode: StatusCodes.Status409Conflict);

            var custom = new ShortLink { Code = request.CustomAlias, OriginalUrl = uri!.ToString(), IsCustomAlias = true, ExpiresAt = request.ExpiresAt, ManageTokenHash = tokenHash };
            db.Links.Add(custom);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                return Results.Problem($"Alias '{request.CustomAlias}' is already taken.", statusCode: StatusCodes.Status409Conflict);
            }
            return Results.Created($"/api/links/{custom.Code}", ToCreatedResponse(custom, http, token));
        }

        // Random code with bounded collision retry. Unique index is the source of truth.
        for (var attempt = 0; attempt < MaxCodeCollisionRetries; attempt++)
        {
            var link = new ShortLink { Code = generator.Generate(), OriginalUrl = uri!.ToString(), ExpiresAt = request.ExpiresAt, ManageTokenHash = tokenHash };
            db.Links.Add(link);
            try
            {
                await db.SaveChangesAsync(ct);
                return Results.Created($"/api/links/{link.Code}", ToCreatedResponse(link, http, token));
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

    private static async Task<IResult> DeleteAsync(string code, AppDbContext db, HttpContext http, CancellationToken ct)
    {
        var link = await db.Links.FirstOrDefaultAsync(l => l.Code == code, ct);
        if (link is null) return Results.NotFound();

        // Ownership: only the holder of the manage token issued at creation may delete. Unknown codes stay 404 (codes are
        // public anyway); a missing or wrong token is 403 and leaves the link untouched.
        var presented = http.Request.Headers[ManageToken.HeaderName].ToString();
        if (!ManageToken.Matches(presented, link.ManageTokenHash))
            return Results.Problem(
                $"A valid manage token is required to delete this link. Send it in the {ManageToken.HeaderName} header; it was returned once when the link was created.",
                statusCode: StatusCodes.Status403Forbidden);

        db.Links.Remove(link);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
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
        return new LinkResponse(link.Code, ShortUrl(link, http), link.OriginalUrl, link.IsCustomAlias, link.CreatedAt, link.ExpiresAt);
    }

    private static CreatedLinkResponse ToCreatedResponse(ShortLink link, HttpContext http, string manageToken)
    {
        return new CreatedLinkResponse(link.Code, ShortUrl(link, http), link.OriginalUrl, link.IsCustomAlias, link.CreatedAt, link.ExpiresAt, manageToken);
    }

    private static string ShortUrl(ShortLink link, HttpContext http) => $"{http.Request.Scheme}://{http.Request.Host}/{link.Code}";

    private static string? Truncate(string? value, int max)
        => string.IsNullOrEmpty(value) ? null : value.Length <= max ? value : value[..max];
}
