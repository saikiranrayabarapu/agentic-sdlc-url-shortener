namespace UrlShortener.Api.Domain;

/// <summary>A shortened link. <see cref="Code"/> is the public identifier used in the short URL.</summary>
public sealed class ShortLink
{
    public long Id { get; set; }
    public required string Code { get; set; }
    public required string OriginalUrl { get; set; }
    public bool IsCustomAlias { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAt { get; set; }

    public bool IsExpired(DateTimeOffset now) => ExpiresAt is { } exp && exp <= now;
}
