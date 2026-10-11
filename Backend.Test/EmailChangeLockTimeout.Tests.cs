using Backend.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;

namespace Backend.Test
{
    // Confirming an email change waits up to 10 s for the address lock. This test holds that lock for
    // the whole wait, with the confirm's transaction open, so it runs alone (other tests could be
    // blocked behind that transaction).
    [Collection(DeadlockCollection.Name)]
    public partial class EmailChangeLockTimeoutTests : IntegrationTestBase
    {
        private readonly CapturingEmailSender _emails = new();
        private readonly WebApplicationFactory<Program> _app;

        public EmailChangeLockTimeoutTests(WebApplicationFactory<Program> factory)
            : base(factory, ("RateLimiting:email:PermitLimit", "1000"), ("EmailChange:ResendCooldownSeconds", "0"))
        {
            _app = WithHost(builder => builder.ConfigureTestServices(services => services.AddSingleton<IEmailSender>(_emails)), Factory);
        }

        [GeneratedRegex(@"/confirm-email\?token=([A-Za-z0-9_-]{43})")]
        private static partial Regex ConfirmLink();

        private async Task<string> WaitForConfirmTokenAsync(string to)
        {
            for (var i = 0; i < 100; i++)
            {
                var match = _emails.Sent
                    .Where(m => m.To.Equals(to, StringComparison.OrdinalIgnoreCase))
                    .Select(m => ConfirmLink().Match(m.Body))
                    .FirstOrDefault(m => m.Success);
                if (match != null)
                    return match.Groups[1].Value;
                await Task.Delay(50);
            }
            throw new TimeoutException($"No confirmation email to {to}");
        }

        [Fact]
        public async Task Confirm_WhenTheAddressLockTimesOut_Answers503_AndTheLinkStaysUsable()
        {
            var client = await NewUserAsync(_app);
            var newEmail = NewEmail();
            (await client.PostAsJsonAsync("/user/email/change", new { email = newEmail, currentPassword = Password }))
                .StatusCode.Should().Be(HttpStatusCode.OK);
            var token = await WaitForConfirmTokenAsync(newEmail);

            // Hold the address lock on a separate connection to the test database
            var connectionString = Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("DefaultConnection");
            await using var holder = new SqlConnection(connectionString);
            await holder.OpenAsync();
            await using var holderTransaction = holder.BeginTransaction();
            await using (var take = new SqlCommand(@"
                DECLARE @Result INT;
                EXEC @Result = sp_getapplock @Resource = @Resource, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 0;
                SELECT @Result;", holder, holderTransaction))
            {
                take.Parameters.AddWithValue("@Resource", "fv-email-change:" + newEmail.ToLowerInvariant());
                Convert.ToInt32(await take.ExecuteScalarAsync()).Should().BeGreaterThanOrEqualTo(0);
            }

            var busy = await client.PostAsJsonAsync("/user/email/confirm", new { token });
            busy.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            busy.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(5));
            (await ReadJsonAsync(busy)).GetProperty("error").GetString().Should().Be("The server is busy. Please try again in a moment.");

            // The failed attempt rolled back its use of the link
            await holderTransaction.RollbackAsync();
            (await client.PostAsJsonAsync("/user/email/confirm", new { token })).StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }
}
