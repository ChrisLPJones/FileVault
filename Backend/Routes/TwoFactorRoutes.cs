using Backend.Models;
using Backend.Services;
using System.Security.Claims;

namespace Backend.Routes
{
    // Two-factor authentication with an authenticator app (TOTP).
    // POST /user/login returns a challenge instead of tokens when 2FA is on; the second step is here.
    public static class TwoFactorRoutes
    {
        public static void MapTwoFactorRoutes(this IEndpointRouteBuilder app)
        {
            // Second login step: exchanges the challenge and a code for tokens, like a normal login
            app.MapPost("/user/login/2fa", async (
                HttpContext http,
                TwoFactorLoginRequest? request,
                TwoFactorService twoFactor,
                AuthServices auth,
                DatabaseServices db) =>
            {
                if (request == null ||
                    (string.IsNullOrWhiteSpace(request.Code) && string.IsNullOrWhiteSpace(request.RecoveryCode)))
                    return Results.BadRequest(new { error = "Enter the code from your authenticator app or a recovery code" });

                var (outcome, userId) = await twoFactor.RedeemLoginChallengeAsync(
                    request.ChallengeToken, request.Code, request.RecoveryCode);

                if (outcome == TwoFactorService.ChallengeOutcome.WrongCode)
                    return Results.Json(new { error = "That code isn't valid" }, statusCode: 401);

                var user = userId == null ? null : await db.GetUserByUserId(userId);
                if (user == null)
                    return Results.Json(new { error = "This login has expired. Please log in again." }, statusCode: 401);

                await auth.IssueRefreshTokenAsync(user.Id.ToString(), db, http);
                return Results.Ok(new { Success = auth.GetJWTToken(user) });
            })
                .WithTags("Account")
                .WithSummary("Finish logging in with two-factor authentication: send the challenge from /user/login with a code (or recovery code)")
                .Produces<TokenResponse>()
                .Produces<ErrorResponse>(400)
                .Produces<ErrorResponse>(401)
                .Produces<ErrorResponse>(429).RequireRateLimiting("two-factor");

            // Whether 2FA is on, and how many recovery codes are left
            app.MapGet("/user/2fa", async (
                ClaimsPrincipal user,
                DatabaseServices db) =>
            {
                var state = await db.GetTwoFactorStateAsync(user.GetUserId());
                if (state == null)
                    return Results.NotFound(new { error = "User not found" });

                return Results.Ok(new TwoFactorStatusResponse(state.Enabled, state.Enabled ? state.RecoveryCodesLeft : 0));
            })
                .WithTags("Two-factor authentication")
                .WithSummary("Get whether two-factor authentication is on and how many recovery codes are left")
                .Produces<TwoFactorStatusResponse>()
                .Produces<ErrorResponse>(404).RequireAuthorization();

            // Step 1 of setup: a new secret for the authenticator app (needs the password)
            app.MapPost("/user/2fa/setup", async (
                ClaimsPrincipal user,
                TwoFactorSetupRequest? request,
                DatabaseServices db,
                TwoFactorService twoFactor) =>
            {
                var userId = user.GetUserId();
                var account = await db.GetUserByUserId(userId);
                if (account == null)
                    return Results.NotFound(new { error = "User not found" });

                if (string.IsNullOrEmpty(request?.Password) || !BCrypt.Net.BCrypt.Verify(request.Password, account.Password))
                    return Results.BadRequest(new { error = "Password is incorrect" });

                var setup = await twoFactor.StartSetupAsync(userId, account.Email);
                if (setup == null)
                    return Results.Conflict(new { error = "Two-factor authentication is already on" });

                return Results.Ok(new TwoFactorSetupResponse(setup.Secret, setup.OtpAuthUri));
            })
                .WithTags("Two-factor authentication")
                .WithSummary("Start setting up two-factor authentication: returns a secret and an otpauth:// link for the QR code")
                .Produces<TwoFactorSetupResponse>()
                .Produces<ErrorResponse>(400)
                .Produces<ErrorResponse>(409)
                .Produces<ErrorResponse>(429).RequireAuthorization().RequireRateLimiting("two-factor");

            // Step 2 of setup: confirm a code from the app, which turns 2FA on and signs out every
            // other session (they were started without a code). This device stays logged in.
            app.MapPost("/user/2fa/enable", async (
                HttpContext http,
                ClaimsPrincipal user,
                TwoFactorCodeRequest? request,
                TwoFactorService twoFactor,
                DatabaseServices db,
                AuthServices auth) =>
            {
                var userId = user.GetUserId();
                var codes = await twoFactor.EnableAsync(userId, request?.Code);
                if (codes == null)
                    return Results.BadRequest(new { error = "That code isn't valid. Check the time on your device and try again." });

                // Without a refresh cookie we can't tell which session is this one, so all are signed out
                var current = await auth.CurrentSessionIdAsync(db, http);
                var others = (await db.GetActiveSessionsAsync(userId)).Count(s => s.Id != current);
                await db.RevokeOtherSessionsAsync(userId, current);

                return Results.Ok(new TwoFactorEnabledResponse(codes, others));
            })
                .WithTags("Two-factor authentication")
                .WithSummary("Turn on two-factor authentication by confirming a code; signs out every other session " +
                    "and returns 10 single-use recovery codes (shown once)")
                .Produces<TwoFactorEnabledResponse>()
                .Produces<ErrorResponse>(400)
                .Produces<ErrorResponse>(429).RequireAuthorization().RequireRateLimiting("two-factor");

            // Turns 2FA off (needs the password and a code or recovery code)
            app.MapPost("/user/2fa/disable", async (
                ClaimsPrincipal user,
                TwoFactorDisableRequest? request,
                DatabaseServices db,
                TwoFactorService twoFactor) =>
            {
                var userId = user.GetUserId();
                var account = await db.GetUserByUserId(userId);
                if (account == null)
                    return Results.NotFound(new { error = "User not found" });

                if (string.IsNullOrEmpty(request?.Password) || !BCrypt.Net.BCrypt.Verify(request.Password, account.Password))
                    return Results.BadRequest(new { error = "Password is incorrect" });

                if (!await twoFactor.VerifyAsync(userId, request.Code, request.RecoveryCode))
                    return Results.BadRequest(new { error = "That code isn't valid" });

                await twoFactor.DisableAsync(userId);
                return Results.Ok(new { success = "Two-factor authentication turned off" });
            })
                .WithTags("Two-factor authentication")
                .WithSummary("Turn off two-factor authentication (needs the password and a code or recovery code)")
                .Produces<SuccessResponse>()
                .Produces<ErrorResponse>(400)
                .Produces<ErrorResponse>(429).RequireAuthorization().RequireRateLimiting("two-factor");

            // New recovery codes (needs a code); the old ones stop working
            app.MapPost("/user/2fa/recovery-codes", async (
                ClaimsPrincipal user,
                TwoFactorCodeRequest? request,
                TwoFactorService twoFactor) =>
            {
                var userId = user.GetUserId();
                if (!await twoFactor.VerifyAsync(userId, request?.Code, null))
                    return Results.BadRequest(new { error = "That code isn't valid" });

                return Results.Ok(new RecoveryCodesResponse(await twoFactor.RegenerateRecoveryCodesAsync(userId)));
            })
                .WithTags("Two-factor authentication")
                .WithSummary("Replace the recovery codes (needs a code from the authenticator app); the old ones stop working")
                .Produces<RecoveryCodesResponse>()
                .Produces<ErrorResponse>(400)
                .Produces<ErrorResponse>(429).RequireAuthorization().RequireRateLimiting("two-factor");
        }
    }
}
