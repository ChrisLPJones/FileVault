using Backend.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Backend.Test
{
    [TestCaseOrderer("Backend.Test.PriorityOrderer", "Backend.Test")]
    public class FileEndpointsTest : IClassFixture<WebApplicationFactory<Program>>
    {
        private readonly HttpClient _client;
        private static string? _jwt;

        public FileEndpointsTest(WebApplicationFactory<Program> factory)
        {
            _client = factory.CreateClient();
        }

        private async Task AuthenticateAsync()
        {
            if (!string.IsNullOrEmpty(_jwt))
            {
                // Apply JWT header to current client
                _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _jwt);
                return;
            }

            // Login to get JWT
            LoginModel loginUser = new()
            {
                Email = "testemail@address.com",
                Password = "testpassword"
            };
            string jsonLogin = JsonSerializer.Serialize(loginUser);
            StringContent loginContent = new(jsonLogin, Encoding.UTF8, "application/json");

            var response = await _client.PostAsync("/user/login", loginContent);
            var content = await response.Content.ReadAsStringAsync();

            using var jsonDoc = JsonDocument.Parse(content);
            _jwt = jsonDoc.RootElement.GetProperty("success").GetString();

            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _jwt);
        }

        [Fact, TestPriority(1)]
        public async Task RegisterUser_ShouldSucceed()
        {
            UserModel registerUser = new()
            {
                Username = "testuser",
                Email = "testemail@address.com",
                Password = "testpassword"
            };
            string json = JsonSerializer.Serialize(registerUser);
            StringContent registerContent = new(json, Encoding.UTF8, "application/json");

            var response = await _client.PostAsync("/user/register", registerContent);
            var content = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            content.Should().Be("{\"success\":\"User testuser registered\"}");
        }

        [Fact, TestPriority(2)]
        public async Task LoginUser_ShouldReturnJwt()
        {
            await AuthenticateAsync();

            _jwt.Should().NotBeNullOrEmpty();
        }

        [Fact, TestPriority(3)]
        public async Task GetUser_ShouldReturnInfo()
        {
            await AuthenticateAsync();

            var response = await _client.GetAsync("/user/info");
            var content = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            content.Should().Be("{\"username\":\"testuser\",\"email\":\"testemail@address.com\"}");
        }

        [Fact, TestPriority(4)]
        public async Task UpdateUser_ShouldSucceed()
        {
            await AuthenticateAsync();

            UserModel updateUser = new()
            {
                Username = "user",
                Email = "test@example.com",
                Password = "asdasd"
            };
            string updateUserJson = JsonSerializer.Serialize(updateUser);
            StringContent updateUserContent = new(updateUserJson, Encoding.UTF8, "application/json");

            var response = await _client.PutAsync("/user", updateUserContent);
            var content = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            content.Should().Be("{\"success\":\"Updated user info\"}");
        }

        [Fact, TestPriority(5)]
        public async Task UploadFile_ShouldSucceed()
        {
            await AuthenticateAsync();

            var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes("Dummy file content"));
            fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("text/plain");

            var multipartContent = new MultipartFormDataContent();
            multipartContent.Add(fileContent, "file", "test.txt");

            var response = await _client.PostAsync("/upload", multipartContent);
            var content = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            content.Should().Be("{\"success\":\"File Uploaded: test.txt\"}");
        }

        [Fact, TestPriority(6)]
        public async Task ListFiles_ShouldContainUploadedFile()
        {
            await AuthenticateAsync();

            var response = await _client.GetAsync("/files");
            var content = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            content.Should().Contain("test.txt");
        }

        [Fact, TestPriority(7)]
        public async Task DownloadFile_ShouldReturnContent()
        {
            await AuthenticateAsync();

            var response = await _client.GetAsync("/download/test.txt");
            var content = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            content.Should().Be("Dummy file content");
        }

        [Fact, TestPriority(8)]
        public async Task DeleteFile_ShouldSucceed()
        {
            await AuthenticateAsync();

            var response = await _client.DeleteAsync("/delete/test.txt");
            var content = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            content.Should().Be("{\"success\":\"File deleted: test.txt\"}");
        }

        [Fact, TestPriority(9)]
        public async Task DeleteUser_ShouldSucceed()
        {
            await AuthenticateAsync();

            var response = await _client.DeleteAsync("/user");
            var content = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            content.Should().Be("{\"message\":\"User's files and account deleted\"}");
        }
    }

    // xUnit Test Priority Attribute & Orderer
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public class TestPriorityAttribute : Attribute
    {
        public int Priority { get; }
        public TestPriorityAttribute(int priority) => Priority = priority;
    }

    public class PriorityOrderer : ITestCaseOrderer
    {
        public IEnumerable<TTestCase> OrderTestCases<TTestCase>(
            IEnumerable<TTestCase> testCases) where TTestCase : ITestCase
        {
            var sortedMethods = testCases.OrderBy(tc =>
            {
                var attr = tc.TestMethod.Method
                    .GetCustomAttributes(typeof(TestPriorityAttribute))
                    .FirstOrDefault();

                return attr == null
                    ? 0
                    : attr.GetNamedArgument<int>("Priority");
            });

            return sortedMethods;
        }
    }
}
