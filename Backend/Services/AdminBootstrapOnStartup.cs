namespace Backend.Services;

// When the API starts: an install that has accounts but no administrator (it predates in-app admin
// management) gets one. Runs here rather than in init.sql because the INITIAL_ADMIN_EMAIL setting
// decides who it may be, and init.sql can't see app settings. Finishes before the API takes requests.
public class AdminBootstrapOnStartup(IServiceScopeFactory scopes, ILogger<AdminBootstrapOnStartup> logger) : IHostedService
{
    private const int MaxAttempts = 5;
    private static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var delay = FirstDelay;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<DatabaseServices>();

                var promoted = await db.EnsureAdministratorAtStartupAsync();
                if (promoted != null && db.InitialAdminEmail == null)
                    logger.LogWarning("No administrator existed: {Email}, the oldest account with a confirmed email, was made one.", promoted);
                else if (promoted != null)
                    logger.LogWarning("No administrator existed: {Email}, the INITIAL_ADMIN_EMAIL account, was made one.", promoted);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (attempt >= MaxAttempts)
                {
                    // The API still starts; the next start tries again
                    logger.LogError(ex, "Could not check that an administrator exists after {Attempts} attempts; the database may be unreachable. " +
                        "The check runs again at the next start.", MaxAttempts);
                    return;
                }

                logger.LogWarning(ex, "Could not check that an administrator exists (attempt {Attempt} of {Max}); retrying in {Delay}s",
                    attempt, MaxAttempts, (int)delay.TotalSeconds);
                try { await Task.Delay(delay, cancellationToken); }
                catch (OperationCanceledException) { return; }
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, MaxDelay.TotalSeconds));
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
