namespace Backend.Services;

// Hosted mode only: warns, then removes, accounts that haven't been used for Hosted:InactiveDays
// (30). "Used" means signed in or had the app open (see DatabaseServices.TouchActivityAsync).
// Administrator, permanent and suspended accounts are skipped.
//
// Two phases per run:
//  1. Warn: accounts inactive for InactiveDays - WarningDays are marked warned in one statement
//     and, if email is set up, a confirmed address is emailed. An address that was never confirmed
//     is not mailed (it could be anyone's).
//  2. Remove: accounts inactive for InactiveDays that were warned at least WarningDays ago, so
//     everyone gets at least WarningDays of notice, even on the day hosted mode is first turned on.
//     Each is checked again inside the delete transaction, so someone who signed in meanwhile is
//     kept. Removal does not depend on the email being delivered.
// Each phase handles at most Hosted:MaxRemovalsPerRun accounts per run.
// Counts removals in the last 24 hours (in memory, per process) for Hosted:MaxRemovalsPerDay
public class RemovalTracker
{
    private readonly object _lock = new();
    private readonly Queue<DateTime> _removals = new();

    public int CountLastDay()
    {
        lock (_lock)
        {
            var cutoff = DateTime.UtcNow.AddDays(-1);
            while (_removals.Count > 0 && _removals.Peek() < cutoff)
                _removals.Dequeue();
            return _removals.Count;
        }
    }

    public void Record()
    {
        lock (_lock)
            _removals.Enqueue(DateTime.UtcNow);
    }
}

public class InactiveAccountService(
    DatabaseServices db,
    RemovalTracker tracker,
    AccountDeletionService deletion,
    AccountEmailService emails,
    IEmailSender emailSender,
    HostedOptions hosted,
    ILogger<InactiveAccountService> logger)
{
    public record RunResult(int Warned, int Removed);

    // onlyUsers narrows the run to those accounts; production passes null (everyone). It exists so
    // tests sharing a database with real accounts can't touch anyone else's.
    public async Task<RunResult> RunAsync(IReadOnlyCollection<Guid>? onlyUsers = null)
    {
        if (!hosted.IsHosted)
            return new RunResult(0, 0);

        // Without an SMTP server the "sender" only writes the email to the log: don't do that for a warning
        var emailIsSetUp = emailSender is not LogEmailSender;

        var warnings = await db.ClaimInactivityWarningsAsync(hosted, hosted.MaxRemovalsPerRun, onlyUsers);
        foreach (var warning in warnings.Where(w => emailIsSetUp && w.EmailVerified))
        {
            var due = DatabaseServices.RemovalDueAt(hosted, false, false, false, warning.LastActiveAt, warning.WarnedAt);
            emails.SendInactivityWarning(warning.Email, warning.FirstName, due ?? warning.WarnedAt.AddDays(hosted.WarningDays),
                hosted.InactiveDays, hosted.ContactEmail);
        }

        if (warnings.Count >= hosted.MaxRemovalsPerRun)
            logger.LogWarning("{Count} accounts were warned in one run (the per-run limit): check that this many inactive accounts is expected",
                warnings.Count);

        var removed = 0;
        var remainingToday = hosted.MaxRemovalsPerDay - tracker.CountLastDay();
        if (remainingToday <= 0)
        {
            logger.LogWarning("Daily limit of {Max} inactive-account removals reached; no more accounts will be removed today",
                hosted.MaxRemovalsPerDay);
            return new RunResult(warnings.Count, 0);
        }

        foreach (var userId in await db.GetAccountsDueForRemovalAsync(hosted, Math.Min(hosted.MaxRemovalsPerRun, remainingToday), onlyUsers))
        {
            try
            {
                var result = await deletion.DeleteAsync(userId, hosted);
                if (result.Success)
                {
                    removed++;
                    tracker.Record();
                }
                else if (result.StatusCode != 409)
                    logger.LogWarning("Removing inactive account {UserId} failed: {Message}", userId, result.Message);
            }
            catch (Exception ex)
            {
                // Keep going: one failure shouldn't stop the others (it is tried again next run)
                logger.LogError(ex, "Removing inactive account {UserId} failed", userId);
            }
        }

        return new RunResult(warnings.Count, removed);
    }
}
