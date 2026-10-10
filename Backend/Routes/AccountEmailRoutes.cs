using Backend.Models;
using Backend.Services;
using System.Security.Claims;

namespace Backend.Routes
{
    // Forgot password and email verification. A new account can't log in until its address is
    // confirmed. Changing the address of a signed-in account is in EmailChangeRoutes (the new
    // address is pending until confirmed); these links only confirm sign-up addresses, and the
    // addresses of accounts that changed theirs before pending changes existed.
    public static class AccountEmailRoutes
    {
        private const string ForgotPasswordReply =
            "If an account uses that email address, a link to reset the password is on its way.";

        private const string ResendReply =
            "If that address belongs to an account that still needs confirming, a new link is on its way.";

        public static void MapAccountEmailRoutes(this IEndpointRouteBuilder app)
        {
            // Emails a password reset link. Always the same answer, so it can't be used to find accounts.
            app.MapPost("/user/forgot-password", async (
                ForgotPasswordRequest request,
                DatabaseServices db,
                AccountEmailService emails) =>
            {
                var email = request?.Email?.Trim();
                if (string.IsNullOrEmpty(email) || email.Length > 100)
                    return Results.BadRequest(new { error = "Enter your email address" });

                var user = await db.GetUserByEmail(email);
                // A suspended account gets nothing, and the answer stays the same
                if (user != null && !(await db.GetUserAuthStateAsync(user.Id.ToString())).Suspended)
                    await emails.SendPasswordResetAsync(user, db);

                return Results.Ok(new { success = ForgotPasswordReply });
            })
                .WithTags("Account")
                .WithSummary("Email a password reset link (always answers the same, whether or not the account exists)")
                .Produces<SuccessResponse>()
                .Produces<ErrorResponse>(400)
                .Produces<ErrorResponse>(429).RequireRateLimiting("email");

            // Sets a new password using the token from a reset email, and signs out every session
            app.MapPost("/user/reset-password", async (
                ResetPasswordRequest request,
                DatabaseServices db,
                AuthServices auth) =>
            {
                // Check the new password first so a weak one doesn't use up the link
                var passwordError = AuthServices.ValidatePassword(request?.NewPassword);
                if (passwordError != null || request?.NewPassword is null)
                    return Results.BadRequest(new { error = passwordError ?? "New password is required" });

                var use = await AccountEmailService.ConsumeAsync(request.Token, AccountEmailService.ResetPurpose, db);
                var user = use == null ? null : await db.GetUserByUserId(use.UserId);
                if (use == null || user == null || (await db.GetUserAuthStateAsync(use.UserId)).Suspended)
                    return Results.BadRequest(new { error = "This reset link is invalid or has expired. Please ask for a new one." });

                await db.UpdatePasswordHashAsync(use.UserId, auth.GeneratePasswordHash(request.NewPassword));
                await db.RevokeAllRefreshTokensAsync(use.UserId);

                // A pending email change may have been started by someone else: drop it
                await db.CancelPendingEmailChangeAsync(use.UserId);

                // The link was opened from the account's inbox, which confirms the address. The
                // password is one the owner just chose, so if this is the INITIAL_ADMIN_EMAIL
                // account and there is no administrator, it becomes one.
                if (string.Equals(use.Email, user.Email, StringComparison.OrdinalIgnoreCase))
                    await db.ConfirmEmailAsync(use.UserId, distrustPasswordIfPromoted: false);

                return Results.Ok(new { success = "Password changed. You can log in with your new password." });
            })
                .WithTags("Account")
                .WithSummary("Choose a new password with the token from a reset email (ends every session)")
                .Produces<SuccessResponse>()
                .Produces<ErrorResponse>(400)
                .Produces<ErrorResponse>(429).RequireRateLimiting("auth");

            // Confirms the email address using the token from a verification email
            app.MapPost("/user/verify-email", async (
                VerifyEmailRequest request,
                DatabaseServices db) =>
            {
                var use = await AccountEmailService.ConsumeAsync(request?.Token, AccountEmailService.VerifyPurpose, db);
                var user = use == null ? null : await db.GetUserByUserId(use.UserId);

                // The link only confirms the address it was sent to, not one changed to since
                if (use == null || user == null || !string.Equals(use.Email, user.Email, StringComparison.OrdinalIgnoreCase))
                    return Results.BadRequest(new { error = "This confirmation link is invalid or has expired." });

                // For the INITIAL_ADMIN_EMAIL account with no administrator this also makes it the
                // administrator and replaces its password: the link proves the mailbox, not who
                // chose the password (see ConfirmEmailAsync), so the owner sets it with Forgot password.
                var promoted = await db.ConfirmEmailAsync(use.UserId, distrustPasswordIfPromoted: true);
                if (promoted)
                    await db.RevokeAllRefreshTokensAsync(use.UserId);
                return Results.Ok(new { success = "Email address confirmed", passwordReset = promoted });
            })
                .WithTags("Account")
                .WithSummary("Confirm the email address with the token from a verification email")
                .Produces<SuccessResponse>()
                .Produces<ErrorResponse>(400)
                .Produces<ErrorResponse>(429).RequireRateLimiting("auth");

            // Sends a new confirmation email from the login page (unverified users can't log in).
            // Always the same answer, so it can't be used to find accounts or their state.
            app.MapPost("/user/resend-verification-email", async (
                ResendVerificationRequest request,
                DatabaseServices db,
                AccountEmailService emails) =>
            {
                var email = request?.Email?.Trim();
                if (string.IsNullOrEmpty(email) || email.Length > 100)
                    return Results.BadRequest(new { error = "Enter your email address" });

                var user = await db.GetUserByEmail(email);
                if (user != null && !await db.IsEmailVerifiedAsync(user.Id.ToString()))
                    await emails.SendVerificationAsync(user, db);

                return Results.Ok(new { success = ResendReply });
            })
                .WithTags("Account")
                .WithSummary("Send another email confirmation link to an address (no login; always answers the same)")
                .Produces<SuccessResponse>()
                .Produces<ErrorResponse>(400)
                .Produces<ErrorResponse>(429).RequireRateLimiting("email");

            // Sends a new verification email to the logged-in user
            app.MapPost("/user/resend-verification", async (
                ClaimsPrincipal principal,
                DatabaseServices db,
                AccountEmailService emails) =>
            {
                var userId = principal.GetUserId();
                var user = await db.GetUserByUserId(userId);
                if (user == null)
                    return Results.NotFound(new { error = "User not found" });

                if (await db.IsEmailVerifiedAsync(userId))
                    return Results.Ok(new { success = "Your email address is already confirmed" });

                await emails.SendVerificationAsync(user, db);
                return Results.Ok(new { success = $"We've sent a new confirmation link to {user.Email}" });
            })
                .WithTags("Account")
                .WithSummary("Send another email address confirmation link")
                .Produces<SuccessResponse>()
                .Produces<ErrorResponse>(429).RequireAuthorization().RequireRateLimiting("email");
        }
    }
}
