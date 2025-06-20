using Backend.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using System.Net;
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
            UserModel user = new()
            {
                Username = "testuser",
                Email = "testemail@address.com",
                Password = "testpassword"
            };
            string json = JsonSerializer.Serialize(user);
            StringContent content = new StringContent(json, Encoding.UTF8, "application/json");

            // Act
            HttpResponseMessage responseMessageRegister = await _client.PostAsync("/user/register", content);
            string responseBody = await responseMessageRegister.Content.ReadAsStringAsync();

            /// Assert
            responseMessageRegister.StatusCode.Should().Be(HttpStatusCode.Created);
            responseBody.Should().Be("\"Successfully added user testuser\"");






            /* Login User */
            // Arrange
            LoginModel userLogin = new()
            {
                Username = "testuser",
                Password = "testpassword"
            };

            string jsonLogin = JsonSerializer.Serialize(userLogin);
            StringContent contentLogin = new(jsonLogin, Encoding.UTF8, "application/json");

            // Act
            HttpResponseMessage responseMessageLogin = await _client.PostAsync("/user/login", contentLogin);
            string responseBodyLogin = await responseMessageLogin.Content.ReadAsStringAsync();
            using var jsonDoc = JsonDocument.Parse(responseBodyLogin);
            string? jwt = jsonDoc.RootElement.GetProperty("success").GetString();
            _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", jwt);

            // Assert
            responseMessageLogin.StatusCode.Should().Be(HttpStatusCode.OK);
            responseBodyLogin.Should().Contain("success");






            /* Get User */
            // Arrange

            // Act 
            HttpResponseMessage responseMessageGetUser = await _client.GetAsync("/user/info");
            string responseBodyGetUser = await responseMessageGetUser.Content.ReadAsStringAsync();

            // Assert
            responseMessageGetUser.StatusCode.Should().Be(HttpStatusCode.OK);
            responseBodyGetUser.Should().Be("{\"username\":\"testuser\",\"email\":\"testemail@address.com\"}");






            /* Update User */
            // Arrange

            // Act

            // Assert






            // /* Upload File */
            // Arrange
            // var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes("Dummy file content"));
            // fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("text/plain");

            // var multipartContent = new MultipartFormDataContent();
            // multipartContent.Add(fileContent, name: "file", fileName: "test.txt");

            // // Act
            // var response = await _client.PostAsync("/upload", multipartContent);

            // // Assert
            // response.StatusCode.Should().Be(HttpStatusCode.OK);
            // var content = await response.Content.ReadAsStringAsync();
            // content.Should().Be("\"File Uploaded: test.txt\"");
            // /* List File */

            // // Act
            // response = await _client.GetAsync("/files");

            // // Assert
            // response.StatusCode.Should().Be(HttpStatusCode.OK);
            // content = await response.Content.ReadAsStringAsync();
            // content.Should().Contain("test.txt");






            // /* Download File */
            // // Act
            // response = await _client.GetAsync("download/test.txt");

            // // Assert
            // response.StatusCode.Should().Be(HttpStatusCode.OK);
            // content = await response.Content.ReadAsStringAsync();
            // content.Should().Be("Dummy file content");






            // /* Delete File */
            // // Act
            // response = await _client.DeleteAsync("/delete/test.txt");

            // // Assert
            // response.StatusCode.Should().Be(HttpStatusCode.OK);
            // content = await response.Content.ReadAsStringAsync();
            // content.Should().Be("\"File deleted: test.txt\"");






            /* Delete User */
            // Arrange

            // Act
            await _client.DeleteAsync($"/user");

            // Assert


        }
    }
}