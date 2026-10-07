namespace UrlShortener.Api.Contracts;

public sealed record CreateLinkRequest(string Url, string? CustomAlias = null, DateTimeOffset? ExpiresAt = null);

public sealed record LinkResponse(string Code, string ShortUrl, string OriginalUrl, bool IsCustomAlias, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt);

/// <summary>
/// Returned once, at creation. <see cref="ManageToken"/> is the secret that authorises deleting the link; the server
/// keeps only its hash, so it can never be retrieved again.
/// </summary>
public sealed record CreatedLinkResponse(string Code, string ShortUrl, string OriginalUrl, bool IsCustomAlias, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, string ManageToken);

public sealed record DailyClicks(DateOnly Date, int Clicks);

public sealed record ReferrerClicks(string Referrer, int Clicks);

public sealed record LinkStatsResponse(
    string Code,
    long TotalClicks,
    DateTimeOffset? LastClickedAt,
    IReadOnlyList<DailyClicks> ClicksPerDay,
    IReadOnlyList<ReferrerClicks> TopReferrers);
