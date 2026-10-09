namespace Backend.Services;

// Administrators come from the Admin:Emails setting (a list of email addresses). When it is set,
// it decides who is an admin: it is applied to every account when the API starts and to each
// user when they log in, so adding or removing an email takes effect then. When it isn't set,
// Users.IsAdmin is left alone (so it can be managed in the database instead).
public class AdminService(IConfiguration config, ILogger<AdminService> logger)
{
    // A list in appsettings ("Emails": ["a@x", ...]), or one comma-separated value such as the
    // Admin__Emails environment variable that docker-compose.yml sets from ADMIN_EMAILS
    public IReadOnlyCollection<string> AdminEmails
    {
        get
        {
            var section = config.GetSection("Admin:Emails");
            var values = section.GetChildren().Select(child => child.Value).Append(section.Value);
            return values
                .SelectMany(value => (value ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    // Apply the list to one user (after logging in) or to everyone (userId null, at startup)
    public async Task SyncAsync(DatabaseServices db, string? userId = null)
    {
        var emails = AdminEmails;
        if (emails.Count == 0)
            return;

        try
        {
            await db.SyncAdminsAsync(emails, userId);
        }
        catch (Exception ex)
        {
            // Never block a login or startup because of this
            logger.LogError(ex, "Applying Admin:Emails failed");
        }
    }
}

// Applies Admin:Emails to every account once the API has started
public class AdminSyncOnStartup(IServiceProvider services) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        var admins = scope.ServiceProvider.GetRequiredService<AdminService>();
        await admins.SyncAsync(scope.ServiceProvider.GetRequiredService<DatabaseServices>());
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
