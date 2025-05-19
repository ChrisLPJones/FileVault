using System.Data.Common;
using Azure.Identity;
using FileVaultBackend.Models;
using Microsoft.AspNetCore.Authentication.OAuth.Claims;

namespace FileVaultBackend.Services
{
    public class AuthServices
    {
        // Add user to database
        internal async Task HashAndRegisterUserAsync(UserModel user, DatabaseServices db)
        {
            string userName = user.UserName;
            string email = user.Email;
            string password = BCrypt.Net.BCrypt.HashPassword(user.Password);

            db.RegisterUser(userName, email, password);
        }

        // Delete user

        // Get password hash

        // Get JWT tokes

        // update token

        // log user out

        // Get user info
    }
}
