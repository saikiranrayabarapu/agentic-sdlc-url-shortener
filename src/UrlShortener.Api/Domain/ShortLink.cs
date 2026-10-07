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

    /// <summary>
    /// SHA-256 (hex) of the manage token issued at creation; the token itself is never stored. Null for links created
    /// before ownership existed, which therefore cannot be deleted through the API.
    /// </summary>
    public string? ManageTokenHash { get; set; }

    public bool IsExpired(DateTimeOffset now) => ExpiresAt is { } exp && exp <= now;
}
