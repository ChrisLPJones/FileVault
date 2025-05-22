using System.Data.Common;
using Azure.Identity;
using FileVaultBackend.Models;
using Microsoft.AspNetCore.Authentication.OAuth.Claims;

namespace FileVaultBackend.Services
{
    public class AuthServices
    {
        // Add user to database
        public async Task<HttpReturnResult> HashAndRegisterUser(UserModel user, DatabaseServices db)
        {

            string password = BCrypt.Net.BCrypt.HashPassword(user.Password);

            return await db.RegisterUser(user.UserName, user.Email, password);
        }

        // Delete user

        // Get password hash

        // Get JWT tokes

        // update token

        // log user out

        // Get user info
    }
}
