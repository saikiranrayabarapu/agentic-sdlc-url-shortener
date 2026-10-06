namespace UrlShortener.Api.Domain;

/// <summary>One redirect through a short link. Recorded asynchronously so the redirect itself stays fast.</summary>
public sealed class ClickEvent
{
    public long Id { get; set; }
    public long LinkId { get; set; }
    /// <summary>UTC. Stored as DateTime because SQLite cannot compare or aggregate DateTimeOffset columns.</summary>
    public DateTime OccurredAtUtc { get; set; }
    public string? Referrer { get; set; }
    public string? UserAgent { get; set; }
}
