using UrlShortener.Api.Data;
using UrlShortener.Api.Domain;

namespace UrlShortener.Api.Analytics;

/// <summary>
/// Background writer that drains <see cref="ClickQueue"/> into SQLite in small batches.
/// On shutdown it stops accepting new events and flushes what is already buffered (graceful drain).
/// </summary>
public sealed class ClickRecorder : BackgroundService
{
    private const int BatchSize = 100;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(250);

    private readonly ClickQueue _queue;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ClickRecorder> _logger;

    public ClickRecorder(ClickQueue queue, IServiceScopeFactory scopes, ILogger<ClickRecorder> logger)
    {
        _queue = queue;
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<ClickEvent>(BatchSize);
        try
        {
            while (await _queue.Reader.WaitToReadAsync(stoppingToken))
            {
                while (batch.Count < BatchSize && _queue.Reader.TryRead(out var click)) batch.Add(click);
                if (batch.Count > 0)
                {
                    await PersistAsync(batch, CancellationToken.None);
                    batch.Clear();
                }
                else
                {
                    await Task.Delay(FlushInterval, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown requested: fall through to the final drain.
        }

        _queue.Complete();
        while (_queue.Reader.TryRead(out var remaining)) batch.Add(remaining);
        if (batch.Count > 0) await PersistAsync(batch, CancellationToken.None);
    }

    private async Task PersistAsync(List<ClickEvent> batch, CancellationToken ct)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.Clicks.AddRange(batch);
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                // SQLite returns "database is locked" under concurrent writers; a short backoff is the standard remedy.
                _logger.LogWarning(ex, "Persisting {Count} click events failed (attempt {Attempt}); retrying", batch.Count, attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Dropping {Count} click events after {Attempts} attempts", batch.Count, maxAttempts);
            }
        }
    }
}
