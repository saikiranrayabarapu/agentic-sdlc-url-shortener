namespace UrlShortener.Api.Contracts;

public sealed record CreateLinkRequest(string Url, string? CustomAlias = null, DateTimeOffset? ExpiresAt = null);

public sealed record LinkResponse(string Code, string ShortUrl, string OriginalUrl, bool IsCustomAlias, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt);

public sealed record DailyClicks(DateOnly Date, int Clicks);

public sealed record ReferrerClicks(string Referrer, int Clicks);

public sealed record LinkStatsResponse(
    string Code,
    long TotalClicks,
    DateTimeOffset? LastClickedAt,
    IReadOnlyList<DailyClicks> ClicksPerDay,
    IReadOnlyList<ReferrerClicks> TopReferrers);
