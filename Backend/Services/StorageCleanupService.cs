namespace Backend.Services;

// Housekeeping that runs in the background: shortly after start-up, then every
// Storage:CleanupIntervalMinutes (default 60). Empties recycle bin entries older than
// Storage:TrashRetentionDays and removes chunked uploads abandoned for 24 hours.
// Failures are logged and retried on the next run.
public class StorageCleanupService(IServiceScopeFactory scopes, IConfiguration config, ILogger<StorageCleanupService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, config.GetValue("Storage:CleanupIntervalMinutes", 60)));

        try
        {
            // Let the app finish starting first
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                await RunOnceAsync();
                await Task.Delay(interval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down
        }
    }

    public async Task RunOnceAsync()
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatabaseServices>();

        try
        {
            var purged = await scope.ServiceProvider.GetRequiredService<TrashService>().PurgeExpiredAsync(db);
            if (purged > 0)
                logger.LogInformation("Emptied {Count} expired recycle bin entries", purged);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Emptying expired recycle bin entries failed");
        }

        try
        {
            var removed = await scope.ServiceProvider.GetRequiredService<ChunkedUploadService>().CleanupAbandonedAsync(db);
            if (removed > 0)
                logger.LogInformation("Removed {Count} abandoned uploads", removed);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Removing abandoned uploads failed");
        }
    }
}
