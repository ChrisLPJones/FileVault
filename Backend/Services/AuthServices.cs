using Backend.Models;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace Backend.Services
{
    public class AuthServices
    {
        public const string RefreshCookieName = "fv_refresh";

        private readonly IConfiguration _config;
        public AuthServices(IConfiguration config)
        {
            _config = config;
        }

        // Returns an error message if the password doesn't meet the policy
        public static string? ValidatePassword(string? password)
        {
            if (string.IsNullOrEmpty(password) || password.Length < 8)
                return "Password must be at least 8 characters";
            // BCrypt only uses the first 72 bytes
            if (Encoding.UTF8.GetByteCount(password) > 72)
                return "Password must be 72 bytes or fewer";
            if (!password.Any(char.IsUpper) || !password.Any(char.IsLower) || !password.Any(char.IsDigit))
                return "Password must contain an uppercase letter, a lowercase letter and a number";
            return null;
        }

        // Returns an error message if the name/email can't be used
        public static string? ValidateAccount(string? firstName, string? lastName, string? email)
        {
            firstName = firstName?.Trim();
            lastName = lastName?.Trim();
            email = email?.Trim();

            if (string.IsNullOrEmpty(firstName) || firstName.Length > 50)
                return "First name is required (50 characters or fewer)";
            // Last name is optional (some people have one name, and accounts from before names existed have none)
            if (lastName?.Length > 50)
                return "Last name must be 50 characters or fewer";
            if (string.IsNullOrEmpty(email) || email.Length > 100)
                return "Email must be 100 characters or fewer";

            var at = email.IndexOf('@');
            if (at < 1 || at != email.LastIndexOf('@') || at == email.Length - 1 || email.Contains(' '))
                return "Email is invalid";

            return null;
        }

        // Hashes the user's password and registers them in the database
        public async Task HashAndRegisterUser(UserModel user, DatabaseServices db)
        {
            string password =  BCrypt.Net.BCrypt.HashPassword(user.Password);
            user.Password = password;
            await db.RegisterUser(user);
        }

        // Hashes a plain-text password using BCrypt
        public string GeneratePasswordHash(string password)
        {
            return BCrypt.Net.BCrypt.HashPassword(password);
        }

        // Generates a short-lived JWT access token for a valid user
        public string GetJWTToken(UserModel user)
        {
            var jwtConfig = _config.GetSection("Jwt");

            var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtConfig["Key"] ?? throw new InvalidOperationException("Jwt:Key is not set.")));
            var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

            var now = DateTime.UtcNow;
            var claims = new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim(JwtRegisteredClaimNames.Email, user.Email),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
                // Issue time to the millisecond, for AccessTokenGate
                new Claim(AccessTokenGate.IssuedAtMillisecondsClaim,
                    new DateTimeOffset(now).ToUnixTimeMilliseconds().ToString(), ClaimValueTypes.Integer64)
            };

            var tokenDescriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(claims),
                IssuedAt = now,
                Expires = now.AddMinutes(jwtConfig.GetValue("ExpireMinutes", 15)),
                Issuer = jwtConfig["Issuer"],
                Audience = jwtConfig["Audience"],
                SigningCredentials = credentials,
            };

            var handler = new JwtSecurityTokenHandler();
            var token = handler.CreateToken(tokenDescriptor);
            return handler.WriteToken(token);
        }

        // Validates credentials by email address.
        // Returns the user, or null if the email/password is wrong.
        // What login answers (with suspended: true) for a suspended account
        public const string SuspendedMessage = "This account has been suspended";

        public async Task<UserModel?> ValidateUser(LoginModel user, DatabaseServices db)
        {
            var identifier = user.Identifier;
            var userRecord = await db.GetUserByEmail(identifier);

            if (userRecord == null)
            {
                // Spend the same time as a real check so response timing doesn't reveal which accounts exist
                BCrypt.Net.BCrypt.Verify(user.Password, DummyHash);
                return null;
            }

            if (!BCrypt.Net.BCrypt.Verify(user.Password, userRecord.Password))
                return null;

            await db.UpdateUserLastLogin(userRecord.Id.ToString());
            return userRecord;
        }

        private static readonly string DummyHash = BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString());



        // Hash a refresh token for storage; the raw token only ever lives in the cookie
        private static string HashToken(string token) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

        private TimeSpan RefreshLifetime => TimeSpan.FromDays(_config.GetValue("Jwt:RefreshDays", 7));

        // Create a refresh token for the user and set it as an httpOnly cookie.
        // Without a session ID this is a new login: it starts a new session (see Active sessions)
        // and retires any refresh token this browser already had.
        public async Task<Guid?> IssueRefreshTokenAsync(string userId, DatabaseServices db, HttpContext http, Guid? sessionId = null,
            DatabaseServices.RefreshTokenUse? rotatedFrom = null)
        {
            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var expiresAt = DateTime.UtcNow.Add(RefreshLifetime);
            var ipAddress = DeviceDescription.IpAddress(http);

            if (sessionId == null)
            {
                if (http.Request.Cookies.TryGetValue(RefreshCookieName, out var previous) && !string.IsNullOrEmpty(previous))
                    await db.RevokeRefreshTokenAsync(HashToken(previous));

                sessionId = await db.CreateSessionAsync(
                    userId, DeviceDescription.FromUserAgent(http.Request.Headers.UserAgent), ipAddress);
            }
            else
            {
                await db.TouchSessionAsync(sessionId.Value, ipAddress);
            }

            // When rotating, the token is stored only if no administrator password change landed
            // since the old one was consumed; null means refused
            if (!await db.StoreRefreshTokenAsync(userId, sessionId.Value, HashToken(token), expiresAt,
                    rotatedFrom != null, rotatedFrom?.UserTokensValidAfter))
                return null;

            // Signing in, and renewing the access token while the app is open, count as use (hosted mode's
            // inactivity clock); written at most about hourly
            await db.TouchActivityAsync(userId);

            http.Response.Cookies.Append(RefreshCookieName, token, RefreshCookieOptions(http, expiresAt));
            return sessionId.Value;
        }

        // The session the request's refresh cookie belongs to, or null without a known cookie
        public async Task<Guid?> CurrentSessionIdAsync(DatabaseServices db, HttpContext http) =>
            http.Request.Cookies.TryGetValue(RefreshCookieName, out var token) && !string.IsNullOrEmpty(token)
                ? await db.GetSessionIdForTokenAsync(HashToken(token))
                : null;

        public static void ClearRefreshCookie(HttpContext http) =>
            http.Response.Cookies.Delete(RefreshCookieName, RefreshCookieOptions(http, null));

        // httpOnly so scripts can't read it; scoped to /user so it's only sent to account endpoints.
        // Secure on HTTPS requests (behind a proxy, see ForwardedHeaders:Enabled), or always with
        // Jwt:SecureRefreshCookie.
        private static CookieOptions RefreshCookieOptions(HttpContext http, DateTime? expiresAt) => new()
        {
            HttpOnly = true,
            Secure = http.Request.IsHttps ||
                http.RequestServices.GetRequiredService<IConfiguration>().GetValue("Jwt:SecureRefreshCookie", false),
            SameSite = SameSiteMode.Strict,
            Path = "/user",
            Expires = expiresAt
        };

        // Exchange the refresh cookie for a new access token and a new refresh cookie.
        // Returns null if the cookie is missing, expired, revoked or unknown.
        public async Task<UserModel?> RotateRefreshTokenAsync(DatabaseServices db, HttpContext http)
        {
            if (!http.Request.Cookies.TryGetValue(RefreshCookieName, out var token) || string.IsNullOrEmpty(token))
                return null;

            var use = await db.ConsumeRefreshTokenAsync(HashToken(token));
            if (use == null)
                return null;

            if (!use.Valid)
            {
                // Signed out on purpose (logout, or the session was signed out from another
                // device): just refuse it. Only a rotated-away token suggests theft.
                if (use.SessionId != null && !use.Replaced)
                    return null;

                // A used token being presented again outside the short grace window (two tabs
                // refreshing at once) suggests it was stolen: end every session for this user.
                var grace = TimeSpan.FromSeconds(_config.GetValue("Jwt:RefreshReuseGraceSeconds", 30));
                if (use.SinceRevoked >= grace)
                    await db.RevokeAllRefreshTokensAsync(use.UserId);
                return null;
            }

            var user = await db.GetUserByUserId(use.UserId);
            if (user == null || (await db.GetUserAuthStateAsync(use.UserId)).Suspended)
                return null;

            // Stay in the same session (tokens from before sessions existed start one now)
            var issued = await IssueRefreshTokenAsync(use.UserId, db, http, use.SessionId
                ?? await db.CreateSessionAsync(use.UserId,
                    DeviceDescription.FromUserAgent(http.Request.Headers.UserAgent), DeviceDescription.IpAddress(http)), use);
            return issued == null ? null : user;
        }

        // Revoke the refresh token in the request's cookie (logout)
        public async Task RevokeRefreshCookieAsync(DatabaseServices db, HttpContext http)
        {
            if (http.Request.Cookies.TryGetValue(RefreshCookieName, out var token) && !string.IsNullOrEmpty(token))
                await db.RevokeRefreshTokenAsync(HashToken(token));

            ClearRefreshCookie(http);
        }
    }
}
