using Microsoft.Data.SqlClient;

namespace Backend.Services;

public partial class DatabaseServices
{
    // Diagnostics:AppLock (default off): when taking an application lock fails (timeout, error
    // -999, ...), write who holds the lock and the state of this connection and transaction to
    // the log and standard error. Read-only and never throws; it does not change what happens
    // next. Set by CI to find out why the admin-membership lock times out there.
    private readonly bool _lockDiagnostics;

    private async Task ReportAppLockFailureAsync(string resource, int result, SqlConnection connection, SqlTransaction transaction)
    {
        if (!_lockDiagnostics)
            return;
        try
        {
            string state;
            try
            {
                state = $"spid={connection.ServerProcessId} connection={connection.State} " +
                        $"transactionStillAttached={transaction.Connection != null}";
                if (connection.State == System.Data.ConnectionState.Open && transaction.Connection != null)
                {
                    await using var probe = new SqlCommand("SELECT @@TRANCOUNT, XACT_STATE()", connection, transaction);
                    await using var reader = await probe.ExecuteReaderAsync();
                    if (await reader.ReadAsync())
                        state += $" trancount={reader.GetInt32(0)} xact_state={reader.GetInt16(1)}";
                }
            }
            catch (Exception ex)
            {
                state = $"state probe failed: {ex.GetType().Name}: {ex.Message}";
            }

            var report = await AppLockDiagnostics.DumpAsync(_connectionString,
                $"sp_getapplock({resource}) returned {result}; {state}");
            _logger.LogWarning("{Report}", report);
        }
        catch
        {
            // diagnostics must never change the outcome
        }
    }
}
