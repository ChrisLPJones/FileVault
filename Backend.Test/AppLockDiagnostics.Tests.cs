using Backend.Models;
using Backend.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;

namespace Backend.Test
{
    // Diagnostics for intermittent "Could not take the admin membership lock" failures in CI.
    // They only watch and report; nothing here changes how locks behave.
    //
    // CI sets FILEVAULT_LOCK_DIAGNOSTICS=1 (watchdog below) and Diagnostics__AppLock=true (the app
    // reports the lock state when sp_getapplock fails, see DatabaseService.LockDiagnostics.cs).
    public static class AppLockWatchdog
    {
        public const string EnabledFlag = "FILEVAULT_LOCK_DIAGNOSTICS";

        // Poll the whole SQL Server for an application lock waited on longer than this, then dump
        // who holds it. The app's own timeout is 10 s, so 3 s catches the holder mid-wait.
        private static readonly TimeSpan WaitThreshold = TimeSpan.FromSeconds(
            double.TryParse(Environment.GetEnvironmentVariable("FILEVAULT_LOCK_WATCHDOG_SECONDS"), out var s) ? s : 3);

        [ModuleInitializer]
        public static void Start()
        {
            if (Environment.GetEnvironmentVariable(EnabledFlag) != "1")
                return;

            var master = new SqlConnectionStringBuilder(TestEnvironment.ConnectionString) { InitialCatalog = "master", Pooling = false }.ConnectionString;
            var thread = new Thread(() => Run(master)) { IsBackground = true, Name = "applock-watchdog" };
            thread.Start();
        }

        private static void Run(string connectionString)
        {
            var lastDump = DateTime.MinValue;
            while (true)
            {
                try
                {
                    Thread.Sleep(1000);
                    if (DateTime.UtcNow - lastDump < TimeSpan.FromSeconds(5))
                        continue;

                    using var connection = new SqlConnection(connectionString);
                    connection.Open();
                    using var command = new SqlCommand(@"
                        SELECT ISNULL(MAX(r.wait_time), 0)
                        FROM sys.dm_tran_locks l
                        JOIN sys.dm_exec_requests r ON r.session_id = l.request_session_id
                        WHERE l.resource_type = 'APPLICATION' AND l.request_status = 'WAIT'", connection);
                    var longestWaitMs = Convert.ToInt32(command.ExecuteScalar());
                    if (longestWaitMs < WaitThreshold.TotalMilliseconds)
                        continue;

                    lastDump = DateTime.UtcNow;
                    var report = AppLockDiagnostics.DumpAsync(connectionString,
                        $"watchdog: an application lock has been waited on for {longestWaitMs} ms").GetAwaiter().GetResult();
                    Console.Out.WriteLine(report);
                }
                catch
                {
                    // the watchdog must never disturb the tests
                }
            }
        }
    }

    // A test that only runs when FILEVAULT_LOCK_SIMULATION=1, so it stays out of the normal suite
    public sealed class SimulationFactAttribute : FactAttribute
    {
        public SimulationFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("FILEVAULT_LOCK_SIMULATION") != "1")
                Skip = "Lock-timeout simulation: set FILEVAULT_LOCK_SIMULATION=1 (and FILEVAULT_LOCK_DIAGNOSTICS=1) to run it";
        }
    }

    public class AppLockDiagnosticsTests(WebApplicationFactory<Program> baseFactory) : IClassFixture<WebApplicationFactory<Program>>
    {
        // Holds fv-admin-membership from another connection, in an open transaction, for longer
        // than the app's 10 s timeout, then makes the first registration (which takes that lock).
        // Expect a 503, the watchdog's holder report while it waits, and the app's own report
        // when it gives up.
        [SimulationFact]
        public async Task HeldAdminLock_IsReportedByTheWatchdogAndTheApp()
        {
            await using var db = await FreshDatabase.CreateAsync();
            Console.Error.WriteLine("SIMULATION database: " + db.Name);

            await using var holder = new SqlConnection(db.ConnectionString);
            await holder.OpenAsync();
            await using var begin = new SqlCommand(@"
                BEGIN TRAN;
                DECLARE @R INT;
                EXEC @R = sp_getapplock @Resource = 'fv-admin-membership', @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 0;
                SELECT @R;", holder);
            ((int)(await begin.ExecuteScalarAsync())!).Should().BeGreaterThanOrEqualTo(0);

            await using var factory = db.CreateFactory(baseFactory).WithWebHostBuilder(builder =>
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                    new Dictionary<string, string?> { ["Diagnostics:AppLock"] = "true" })));
            var client = factory.CreateClient();

            var response = await client.PostAsJsonAsync("/user/register",
                new UserModel { FirstName = "Held", LastName = "Lock", Email = $"held_{Guid.NewGuid():N}@example.test", Password = TestAccounts.Password });

            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

            await using var rollback = new SqlCommand("ROLLBACK", holder);
            await rollback.ExecuteNonQueryAsync();
        }
    }
}
