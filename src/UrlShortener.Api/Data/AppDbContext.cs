using Microsoft.EntityFrameworkCore;
using UrlShortener.Api.Domain;

namespace UrlShortener.Api.Data;

public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<ShortLink> Links => Set<ShortLink>();
    public DbSet<ClickEvent> Clicks => Set<ClickEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ShortLink>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Code).HasMaxLength(32).IsRequired();
            e.Property(x => x.OriginalUrl).HasMaxLength(2048).IsRequired();
            e.HasIndex(x => x.Code).IsUnique();
        });

        modelBuilder.Entity<ClickEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Referrer).HasMaxLength(2048);
            e.Property(x => x.UserAgent).HasMaxLength(512);
            e.HasIndex(x => new { x.LinkId, x.OccurredAtUtc });
            e.HasOne<ShortLink>().WithMany().HasForeignKey(x => x.LinkId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
