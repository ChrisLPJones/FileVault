using Microsoft.Data.SqlClient;

namespace Backend.Services;

public partial class DatabaseServices
{
    private const int DeadlockErrorNumber = 1205;

    // SQL error 1205, or losing the admin-membership lock as a deadlock victim (sp_getapplock -3)
    private static bool IsDeadlock(Exception ex) =>
        ex is SqlException { Number: DeadlockErrorNumber } or AppLockDeadlockException;

    // Run a database action again if SQL Server picked it as a deadlock victim. Deletes can
    // deadlock with each other when several users delete at once (the delete triggers walk the
    // Files table), and so can the recycle bin and move updates that walk the same tree, and
    // reads across every user's files (the admin page) with those deletes; the victim's work is
    // rolled back, so trying again is safe. A transaction must be started inside the action.
    private async Task RetryOnDeadlockAsync(Func<Task> action, int attempts = 4) =>
        await RetryOnDeadlockAsync(async () =>
        {
            await action();
            return true;
        }, attempts);

    private async Task<T> RetryOnDeadlockAsync<T>(Func<Task<T>> action, int attempts = 4)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await action();
            }
            catch (Exception ex) when (IsDeadlock(ex) && attempt < attempts)
            {
                _logger.LogInformation("Deadlock on attempt {Attempt}; retrying", attempt);
                await Task.Delay(Random.Shared.Next(20, 80) * attempt);
            }
        }
    }
}
