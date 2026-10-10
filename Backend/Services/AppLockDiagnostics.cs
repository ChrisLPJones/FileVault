using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Backend.Services;

// Read-only diagnostics for sp_getapplock trouble: who holds or waits for each application lock,
// how old their transaction is, what they last ran, and who is blocked behind whom. It only reads
// the SQL Server DMVs (needs VIEW SERVER STATE) and never takes or releases a lock. Used by
// DatabaseServices when Diagnostics:AppLock is on (off by default) and by the test watchdog.
public static partial class AppLockDiagnostics
{
    private const string LocksSql = @"
        SELECT DB_NAME(l.resource_database_id) AS db, l.resource_description AS resource, l.request_mode AS mode,
               l.request_status AS status, l.request_session_id AS spid,
               s.status AS session_status, s.open_transaction_count AS open_tran,
               DATEDIFF(SECOND, tx.began, SYSDATETIME()) AS tran_age_s,
               DATEDIFF(SECOND, s.last_request_start_time, SYSDATETIME()) AS last_request_age_s,
               r.blocking_session_id AS blocked_by, r.wait_type, r.wait_time AS wait_ms,
               COALESCE(rt.text, ct.text) AS sql_text
        FROM sys.dm_tran_locks l
        LEFT JOIN sys.dm_exec_sessions s ON s.session_id = l.request_session_id
        LEFT JOIN sys.dm_exec_requests r ON r.session_id = l.request_session_id
        LEFT JOIN sys.dm_exec_connections c ON c.session_id = l.request_session_id
        OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) rt
        OUTER APPLY sys.dm_exec_sql_text(c.most_recent_sql_handle) ct
        OUTER APPLY (SELECT MIN(a.transaction_begin_time) AS began
                     FROM sys.dm_tran_session_transactions st
                     JOIN sys.dm_tran_active_transactions a ON a.transaction_id = st.transaction_id
                     WHERE st.session_id = l.request_session_id) tx
        WHERE l.resource_type = 'APPLICATION'
        ORDER BY l.resource_database_id, l.resource_description, l.request_status;

        SELECT TOP 50 r.session_id AS spid, r.blocking_session_id AS blocked_by, r.wait_type, r.wait_time AS wait_ms,
               r.wait_resource, DB_NAME(r.database_id) AS db, st.text AS sql_text
        FROM sys.dm_exec_requests r
        OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) st
        WHERE r.blocking_session_id <> 0
        ORDER BY r.wait_time DESC;";

    // A text report of every application lock on the server (held and waited for) and every
    // blocked request. Never throws; a failure is reported in the text.
    public static async Task<string> DumpAsync(string connectionString, string title)
    {
        var text = new StringBuilder();
        text.AppendLine($"=== APPLOCK DIAGNOSTICS {DateTime.UtcNow:O}: {title}");
        try
        {
            // Always from a new connection, so it works when the caller's transaction is dead
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new SqlCommand(LocksSql, connection) { CommandTimeout = 10 };
            await using var reader = await command.ExecuteReaderAsync();

            text.AppendLine("-- application locks (GRANT = holder, WAIT = waiter)");
            await AppendRowsAsync(reader, text);
            await reader.NextResultAsync();
            text.AppendLine("-- blocked requests (blocking chain)");
            await AppendRowsAsync(reader, text);
        }
        catch (Exception ex)
        {
            text.AppendLine("diagnostics query failed: " + ex.Message);
        }
        text.AppendLine("=== END APPLOCK DIAGNOSTICS");
        return text.ToString();
    }

    private static async Task AppendRowsAsync(SqlDataReader reader, StringBuilder text)
    {
        var rows = 0;
        while (await reader.ReadAsync())
        {
            rows++;
            var fields = new List<string>();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var value = reader.IsDBNull(i) ? "null" : Convert.ToString(reader.GetValue(i)) ?? "";
                if (reader.GetName(i) == "sql_text")
                {
                    value = Whitespace().Replace(value, " ").Trim();
                    if (value.Length > 300)
                        value = value[..300] + "...";
                }
                fields.Add($"{reader.GetName(i)}={value}");
            }
            text.AppendLine("  " + string.Join(" | ", fields));
        }
        if (rows == 0)
            text.AppendLine("  (none)");
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
