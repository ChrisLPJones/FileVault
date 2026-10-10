using Backend.Models;
using Backend.Services;
using System.Security.Claims;

namespace Backend.Routes
{
    // Changing a signed-in account's email address. The new address is only pending (it is not the
    // login, and it does not block anyone registering it) until its owner opens the emailed link
    // while signed in as the account that asked. See DatabaseService.EmailChange.cs.
    public static class EmailChangeRoutes
    {
        private const string InvalidLinkMessage =
            "This confirmation link is invalid, has expired, or was sent for a different account. " +
            "Sign in as the account that asked for the change and open the link again, or ask for a new one.";

        // Sends the link to the new address unless the account has asked for too many lately
        // (EmailChange:MaxPerDay (5) links per 24 hours, EmailChange:ResendCooldownSeconds (60) between
        // them). The check and the storing are one step, so parallel requests can't all pass it.
        // Returns the expiry, or the 429 result.
        private static async Task<(DateTime? Expires, IResult? Limited)> TrySendConfirmationAsync(
            Backend.Models.UserModel account, string email, DatabaseServices db, AccountEmailService emails, IConfiguration config)
        {
            var maxPerDay = config.GetValue("EmailChange:MaxPerDay", 5);
            var cooldown = TimeSpan.FromSeconds(config.GetValue("EmailChange:ResendCooldownSeconds", 60));

            var (outcome, expires) = await emails.TrySendEmailChangeConfirmationAsync(account, email, db, maxPerDay, cooldown);
            return outcome switch
            {
                DatabaseServices.EmailChangeStoreOutcome.DailyLimit => (null,
                    Results.Json(new { error = "Too many email change requests today. Please try again later." }, statusCode: 429)),
                DatabaseServices.EmailChangeStoreOutcome.NotFound => (null,
                    Results.NotFound(new { error = "User not found" })),
                DatabaseServices.EmailChangeStoreOutcome.Cooldown => (null,
                    Results.Json(new { error = "Please wait a minute before asking for another link." }, statusCode: 429)),
                _ => (expires, null)
            };
        }

        public static void MapEmailChangeRoutes(this IEndpointRouteBuilder app)
        {
            // Starts a change: emails a confirmation link to the new address (and, if the current
            // address is confirmed, a notice with a cancel link to it). Nothing else changes yet.
            app.MapPost("/user/email/change", async (
                ClaimsPrincipal user,
                ChangeEmailRequest request,
                DatabaseServices db,
                AccountEmailService emails,
                IConfiguration config) =>
            {
                var userId = user.GetUserId();
                var account = await db.GetUserByUserId(userId);
                if (account == null)
                    return Results.NotFound(new { error = "User not found" });

                var email = request?.Email?.Trim().ToLowerInvariant();
                var validationError = AuthServices.ValidateAccount(account.FirstName, account.LastName, email);
                if (validationError != null || email is null)
                    return Results.BadRequest(new { error = validationError ?? "Invalid JSON" });

                // Re-authenticate: a stolen access token or an unattended session must not be enough to move the account
                if (string.IsNullOrEmpty(request?.CurrentPassword) ||
                    !BCrypt.Net.BCrypt.Verify(request.CurrentPassword, account.Password))
                    return Results.BadRequest(new { error = "Current password is incorrect" });

                if (string.Equals(email, account.Email, StringComparison.OrdinalIgnoreCase))
                    return Results.BadRequest(new { error = "That's already your email address" });

                // Only real accounts count; other people's pending addresses are not stored anywhere that conflicts
                var conflict = await db.FindAccountConflictAsync(email, userId);
                if (conflict != null)
                    return Results.Conflict(new { error = conflict });

                // Asking again for the address already pending just sends its link again
                var pending = await db.GetPendingEmailAsync(userId);
                var again = pending != null && string.Equals(pending.Email, email, StringComparison.OrdinalIgnoreCase);

                var (expires, limited) = await TrySendConfirmationAsync(account, email, db, emails, config);
                if (limited != null)
                    return limited;
                if (!again && await db.IsEmailVerifiedAsync(userId))
                    await emails.SendEmailChangeRequestedNoticeAsync(account, email, db);

                return Results.Ok(new EmailChangeRequestedResponse($"We've sent a confirmation link to {email}", email, expires!.Value));
            })
                .WithTags("Account")
                .WithSummary("Ask to change the email address (needs the current password). The new address is pending until " +
                    "its emailed link is confirmed with POST /user/email/confirm; until then the current address stays the login")
                .Produces<EmailChangeRequestedResponse>()
                .Produces<ErrorResponse>(400)
                .Produces<ErrorResponse>(409)
                .Produces<ErrorResponse>(429).RequireAuthorization().RequireRateLimiting("email");

            // Sends the pending address its link again
            app.MapPost("/user/email/resend", async (
                ClaimsPrincipal user,
                DatabaseServices db,
                AccountEmailService emails,
                IConfiguration config) =>
            {
                var userId = user.GetUserId();
                var account = await db.GetUserByUserId(userId);
                if (account == null)
                    return Results.NotFound(new { error = "User not found" });

                var pending = await db.GetPendingEmailAsync(userId);
                if (pending == null)
                    return Results.BadRequest(new { error = "There is no email change waiting to be confirmed" });

                var conflict = await db.FindAccountConflictAsync(pending.Email, userId);
                if (conflict != null)
                    return Results.Conflict(new { error = conflict });

                var (expires, limited) = await TrySendConfirmationAsync(account, pending.Email, db, emails, config);
                if (limited != null)
                    return limited;
                return Results.Ok(new EmailChangeRequestedResponse($"We've sent a new confirmation link to {pending.Email}", pending.Email, expires!.Value));
            })
                .WithTags("Account")
                .WithSummary("Send the confirmation link for the pending email change again")
                .Produces<EmailChangeRequestedResponse>()
                .Produces<ErrorResponse>(400)
                .Produces<ErrorResponse>(409)
                .Produces<ErrorResponse>(429).RequireAuthorization().RequireRateLimiting("email");

            // Drops the pending change; its links stop working
            app.MapDelete("/user/email/pending", async (ClaimsPrincipal user, DatabaseServices db) =>
            {
                await db.CancelPendingEmailChangeAsync(user.GetUserId());
                return Results.Ok(new { success = "Email change cancelled" });
            })
                .WithTags("Account")
                .WithSummary("Cancel the pending email change (does nothing if there is none)")
                .Produces<SuccessResponse>().RequireAuthorization();

            // Completes the change with the token from the email sent to the new address. Only the
            // account that asked for it can: opening someone else's link (even the real owner of
            // the address) does nothing.
            app.MapPost("/user/email/confirm", async (
                ClaimsPrincipal user,
                EmailChangeTokenRequest request,
                DatabaseServices db,
                AuthServices auth,
                AccountEmailService emails) =>
            {
                var hash = AccountEmailService.HashIfWellFormed(request?.Token);
                if (hash == null)
                    return Results.BadRequest(new { error = InvalidLinkMessage });

                var userId = user.GetUserId();
                var result = await db.ConfirmEmailChangeAsync(userId, hash);
                if (result.Outcome == DatabaseServices.EmailChangeOutcome.Taken)
                    return Results.Conflict(new { error = "That address now belongs to another account" });
                if (result.Outcome != DatabaseServices.EmailChangeOutcome.Changed)
                    return Results.BadRequest(new { error = InvalidLinkMessage });

                var account = await db.GetUserByUserId(userId);
                if (account == null)
                    return Results.NotFound(new { error = "User not found" });

                // Tell the old mailbox, if it was a confirmed one (no link: the cancel link died with the change)
                if (result.OldEmailWasVerified && result.OldEmail != null)
                    emails.SendEmailChangedNotice(result.OldEmail, account.FirstName, account.Email);

                // New access token so the email claim is current; sessions are kept
                return Results.Ok(new EmailChangeConfirmedResponse("Email address changed", account.Email, auth.GetJWTToken(account)));
            })
                .WithTags("Account")
                .WithSummary("Confirm the pending email change with the token from the email sent to the new address (sign in as the account that asked)")
                .Produces<EmailChangeConfirmedResponse>()
                .Produces<ErrorResponse>(400)
                .Produces<ErrorResponse>(409)
                .Produces<ErrorResponse>(429).RequireAuthorization().RequireRateLimiting("auth");

            // "This wasn't me" link sent to the old address: cancels the change and signs out every device
            app.MapPost("/user/email/cancel", async (
                EmailChangeTokenRequest request,
                DatabaseServices db,
                AccessTokenGate tokenGate) =>
            {
                var use = await AccountEmailService.ConsumeAsync(request?.Token, AccountEmailService.CancelChangePurpose, db);
                if (use == null)
                    return Results.BadRequest(new { error = "This link is invalid or has expired." });

                await db.CancelEmailChangeAndSignOutAsync(use.UserId);
                tokenGate.Evict(use.UserId); // access tokens already issued stop working now

                return Results.Ok(new { success = "The email change was cancelled and every device has been signed out. We recommend resetting your password." });
            })
                .WithTags("Account")
                .WithSummary("Cancel a pending email change with the token from the notice sent to the old address, and sign out every device")
                .Produces<SuccessResponse>()
                .Produces<ErrorResponse>(400)
                .Produces<ErrorResponse>(429).RequireRateLimiting("auth");
        }
    }
}
