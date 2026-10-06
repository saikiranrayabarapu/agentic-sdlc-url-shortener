namespace UrlShortener.Api.Contracts;

public sealed record CreateLinkRequest(string Url, string? CustomAlias = null, DateTimeOffset? ExpiresAt = null);

public sealed record LinkResponse(string Code, string ShortUrl, string OriginalUrl, bool IsCustomAlias, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt);
