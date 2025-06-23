using Backend.Models;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace Backend.Services
{
    public class AuthServices
    {





        private readonly IConfiguration _config;





        public AuthServices(IConfiguration config)
        {
            _config = config;
        }






        // Hashes the user's password and registers them in the database
        public async Task<HttpReturnResult> HashAndRegisterUser(UserModel user, DatabaseServices db)
        {
            string password = GeneratePasswordHash(user.Password);
            return await db.RegisterUser(user.Username, user.Email, password);
        }






        // Hashes a plain-text password using BCrypt
        public string GeneratePasswordHash(string password)
        {
            return BCrypt.Net.BCrypt.HashPassword(password);
        }






        // Generates a JWT token for a valid user
        public string GetJWTToken(UserModel user)
        {
            var jwtConfig = _config.GetSection("Jwt");

            var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtConfig["Key"]));
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
                Expires = DateTime.UtcNow.AddMinutes(Convert.ToDouble(jwtConfig["ExpireMinutes"])),
                Issuer = jwtConfig["Issuer"],
                Audience = jwtConfig["Audience"],
                SigningCredentials = credentials,
            };

            var handler = new JwtSecurityTokenHandler();
            var token = handler.CreateToken(tokenDescriptor);
            return handler.WriteToken(token);
        }






        // Validates user credentials and returns a JWT if successful
        public async Task<HttpReturnResult> ValidateUser(LoginModel user, DatabaseServices db, AuthServices auth)
        {
            var userRecord = await db.GetUserByUsername(user.Username);
            if (userRecord == null)
                return new HttpReturnResult(false, "User not found");

            bool isValid = BCrypt.Net.BCrypt.Verify(user.Password, userRecord.Password);
            if (!isValid)
                return new HttpReturnResult(false, "Password incorrect");

            string token = auth.GetJWTToken(userRecord);

            await db.UpdateUserLastLogin(user);

            return new HttpReturnResult(true, token);
        }






        // TODO: implement token refresh






        // TODO: implement logout
    }
}
