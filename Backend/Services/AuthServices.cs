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
            if (string.IsNullOrEmpty(lastName) || lastName.Length > 50)
                return "Last name is required (50 characters or fewer)";
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

            var claims = new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim(JwtRegisteredClaimNames.Email, user.Email),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
            };

            var tokenDescriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(claims),
                Expires = DateTime.UtcNow.AddMinutes(jwtConfig.GetValue("ExpireMinutes", 15)),
                Issuer = jwtConfig["Issuer"],
                Audience = jwtConfig["Audience"],
                SigningCredentials = credentials,
            };

            var handler = new JwtSecurityTokenHandler();
            var token = handler.CreateToken(tokenDescriptor);
            return handler.WriteToken(token);
        }

        // Validates user credentials; returns the user, or null if the email/password is wrong
        // Validates credentials by email address.
        // Returns the user, or null if the email/password is wrong.
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

        // Create a refresh token for the user and set it as an httpOnly cookie
        public async Task IssueRefreshTokenAsync(string userId, DatabaseServices db, HttpContext http)
        {
            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var expiresAt = DateTime.UtcNow.Add(RefreshLifetime);

            await db.StoreRefreshTokenAsync(userId, HashToken(token), expiresAt);

            http.Response.Cookies.Append(RefreshCookieName, token, RefreshCookieOptions(http, expiresAt));
        }

        public static void ClearRefreshCookie(HttpContext http) =>
            http.Response.Cookies.Delete(RefreshCookieName, RefreshCookieOptions(http, null));

        // httpOnly so scripts can't read it; scoped to /user so it's only sent to refresh/logout
        private static CookieOptions RefreshCookieOptions(HttpContext http, DateTime? expiresAt) => new()
        {
            HttpOnly = true,
            Secure = http.Request.IsHttps,
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
                // A used token being presented again outside the short grace window (two tabs
                // refreshing at once) suggests it was stolen: end every session for this user.
                var grace = TimeSpan.FromSeconds(_config.GetValue("Jwt:RefreshReuseGraceSeconds", 30));
                if (use.SinceRevoked >= grace)
                    await db.RevokeAllRefreshTokensAsync(use.UserId);
                return null;
            }

            var user = await db.GetUserByUserId(use.UserId);
            if (user == null)
                return null;

            await IssueRefreshTokenAsync(use.UserId, db, http);
            return user;
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
