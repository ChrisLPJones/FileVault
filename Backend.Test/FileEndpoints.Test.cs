using Backend.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Backend.Test
{
    public class FileEndpointsTest : IClassFixture<WebApplicationFactory<Program>>
    {
        private readonly HttpClient _client;

        public FileEndpointsTest(WebApplicationFactory<Program> factory)

        {
            _client = factory.CreateClient();
        }

        [Fact]
        public async Task Upload_List_Download_Delete_WithValidRequest_ReturnsSuccess()
        {
            /* Register User */
            // Arrange
            UserModel registerUser = new()
            {
                Username = "testuser",
                Email = "testemail@address.com",
                Password = "testpassword"
            };
            string json = JsonSerializer.Serialize(registerUser);
            StringContent registerContent = new StringContent(json, Encoding.UTF8, "application/json");

            // Act
            HttpResponseMessage registerResponseMessage = await _client.PostAsync("/user/register", registerContent);
            string registerResponseBody = await registerResponseMessage.Content.ReadAsStringAsync();

            /// Assert
            registerResponseMessage.StatusCode.Should().Be(HttpStatusCode.OK);
            registerResponseBody.Should().Be("{\"success\":\"User testuser registered\"}");

            /* Login User */
            // Arrange
            LoginModel loginUser = new()
            {
                Username = "testuser",
                Password = "testpassword"
            };

            string jsonLogin = JsonSerializer.Serialize(loginUser);
            StringContent loginContent = new(jsonLogin, Encoding.UTF8, "application/json");

            // Act
            HttpResponseMessage loginResponseMessage = await _client.PostAsync("/user/login", loginContent);
            string loginResponseBody = await loginResponseMessage.Content.ReadAsStringAsync();

            using var jsonDoc = JsonDocument.Parse(loginResponseBody);
            string? jwt = jsonDoc.RootElement.GetProperty("success").GetString();
            _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", jwt);

            // Assert
            loginResponseMessage.StatusCode.Should().Be(HttpStatusCode.OK);
            loginResponseBody.Should().Contain("success");

            /* Get User */
            // Act 
            HttpResponseMessage getUserResponseMessage = await _client.GetAsync("/user/info");
            string GetUserResponseBody = await getUserResponseMessage.Content.ReadAsStringAsync();

            // Assert
            getUserResponseMessage.StatusCode.Should().Be(HttpStatusCode.OK);
            GetUserResponseBody.Should().Be("{\"username\":\"testuser\",\"email\":\"testemail@address.com\"}");

            /* Update User */
            // Arrange
            UserModel updateUser = new()
            {
                Username = "user",
                Email = "test@example.com",
                Password = "asdasd"
            };
            string updateUserJson = JsonSerializer.Serialize(updateUser);
            StringContent updateUserContent = new(updateUserJson, Encoding.UTF8, "application/json");

            // Act
            HttpResponseMessage updateUserResponseMessage = await _client.PutAsync("/user", updateUserContent);
            string updateUserResponseBody = await updateUserResponseMessage.Content.ReadAsStringAsync();


            // Assert
            updateUserResponseMessage.StatusCode.Should().Be(HttpStatusCode.OK);
            updateUserResponseBody.Should().Be("{\"success\":\"Updated user info\"}");

            /* Upload File */
            //Arrange
            var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes("Dummy file content"));
            fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("text/plain");

            var multipartContent = new MultipartFormDataContent();
            multipartContent.Add(fileContent, name: "file", fileName: "test.txt");

            // Act
            var response = await _client.PostAsync("/upload", multipartContent);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var content = await response.Content.ReadAsStringAsync();
            content.Should().Be("{\"success\":\"File Uploaded: test.txt\"}");
            /* List File */

            // Act
            response = await _client.GetAsync("/files");
            content = await response.Content.ReadAsStringAsync();

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            content.Should().Contain("test.txt");

            /* Download File */
            // Act
            response = await _client.GetAsync("download/test.txt");
            content = await response.Content.ReadAsStringAsync();

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            content.Should().Be("Dummy file content");

            /* Delete File */
            // Act
            response = await _client.DeleteAsync("/delete/test.txt");
            content = await response.Content.ReadAsStringAsync();

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            content.Should().Be("{\"success\":\"File deleted: test.txt\"}");

            /* Delete User */
            // Arrange

            // Act
            HttpResponseMessage deleteUserResponseMessage = await _client.DeleteAsync($"/user");
            string deleteUserResponseContent = await deleteUserResponseMessage.Content.ReadAsStringAsync();

            // Assert
            deleteUserResponseMessage.StatusCode.Should().Be(HttpStatusCode.OK);
            deleteUserResponseContent.Should().Be("{\"message\":\"User's files and account deleted\"}");
        }
    }
}