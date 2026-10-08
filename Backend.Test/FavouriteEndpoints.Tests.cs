using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Backend.Test
{
    public class FavouriteEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
    {
        private readonly WebApplicationFactory<Program> _factory;

        public FavouriteEndpointsTests(WebApplicationFactory<Program> factory) => _factory = TestAccounts.WithoutLoginLimit(factory);

        private Task<TestAccounts.Account> NewUserAsync() => TestAccounts.CreateAsync(_factory.CreateClient(), "fav");

        private static Task<string> UploadTextAsync(HttpClient client, string name) =>
            TestAccounts.UploadAsync(client, name, Encoding.UTF8.GetBytes($"contents of {name}"), "text/plain");

        [Fact]
        public async Task NewItems_AreNotFavourites_AndHaveNotBeenOpened()
        {
            var (client, _, _) = await NewUserAsync();
            try
            {
                var id = await UploadTextAsync(client, "fresh.txt");
                var item = await TestAccounts.GetItemAsync(client, id);
                item.GetProperty("isFavourite").GetBoolean().Should().BeFalse();
                item.GetProperty("lastOpenedAt").ValueKind.Should().Be(JsonValueKind.Null);

                // The default folders too
                (await TestAccounts.ListAsync(client)).Should().OnlyContain(f => !f.GetProperty("isFavourite").GetBoolean());
            }
            finally
            {
                await client.DeleteAsync("/user");
            }
        }

        [Fact]
        public async Task Favourite_CanBeAddedTwice_AndRemoved_ForFilesAndFolders()
        {
            var (client, _, _) = await NewUserAsync();
            try
            {
                var fileId = await UploadTextAsync(client, "star.txt");
                var folderId = await TestAccounts.CreateFolderAsync(client, "Starred folder");

                foreach (var id in new[] { fileId, folderId })
                {
                    (await client.PutAsync($"/files/{id}/favourite", null)).StatusCode.Should().Be(HttpStatusCode.OK);
                    (await client.PutAsync($"/files/{id}/favourite", null)).StatusCode.Should().Be(HttpStatusCode.OK);
                    (await TestAccounts.GetItemAsync(client, id)).GetProperty("isFavourite").GetBoolean().Should().BeTrue();
                }

                (await client.DeleteAsync($"/files/{fileId}/favourite")).StatusCode.Should().Be(HttpStatusCode.OK);
                (await TestAccounts.GetItemAsync(client, fileId)).GetProperty("isFavourite").GetBoolean().Should().BeFalse();
                (await TestAccounts.GetItemAsync(client, folderId)).GetProperty("isFavourite").GetBoolean().Should().BeTrue();

                // Removing one that isn't a favourite is fine too
                (await client.DeleteAsync($"/files/{fileId}/favourite")).StatusCode.Should().Be(HttpStatusCode.OK);
            }
            finally
            {
                await client.DeleteAsync("/user");
            }
        }

        [Fact]
        public async Task Opened_RecordsTheTime_AndMovesForwardOnEachOpen()
        {
            var (client, _, _) = await NewUserAsync();
            try
            {
                var id = await UploadTextAsync(client, "report.txt");
                var before = DateTime.UtcNow.AddMinutes(-1);

                (await client.PostAsync($"/files/{id}/opened", null)).StatusCode.Should().Be(HttpStatusCode.OK);
                var first = (await TestAccounts.GetItemAsync(client, id)).GetProperty("lastOpenedAt").GetDateTime();
                first.Kind.Should().Be(DateTimeKind.Utc);
                first.Should().BeAfter(before).And.BeBefore(DateTime.UtcNow.AddMinutes(1));

                await Task.Delay(20);
                (await client.PostAsync($"/files/{id}/opened", null)).StatusCode.Should().Be(HttpStatusCode.OK);
                var second = (await TestAccounts.GetItemAsync(client, id)).GetProperty("lastOpenedAt").GetDateTime();
                second.Should().BeAfter(first);
            }
            finally
            {
                await client.DeleteAsync("/user");
            }
        }

        [Fact]
        public async Task Endpoints_RequireLogin()
        {
            var anonymous = _factory.CreateClient();
            var id = Guid.NewGuid();

            (await anonymous.PutAsync($"/files/{id}/favourite", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await anonymous.DeleteAsync($"/files/{id}/favourite")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await anonymous.PostAsync($"/files/{id}/opened", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Fact]
        public async Task OtherUsersItems_AndUnknownIds_AreNotFound_AndUnchanged()
        {
            var owner = await NewUserAsync();
            var other = await NewUserAsync();
            try
            {
                var id = await UploadTextAsync(owner.Client, "private.txt");

                foreach (var target in new[] { id, Guid.NewGuid().ToString(), "not-a-guid" })
                {
                    (await other.Client.PutAsync($"/files/{target}/favourite", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
                    (await other.Client.DeleteAsync($"/files/{target}/favourite")).StatusCode.Should().Be(HttpStatusCode.NotFound);
                    (await other.Client.PostAsync($"/files/{target}/opened", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
                }

                var item = await TestAccounts.GetItemAsync(owner.Client, id);
                item.GetProperty("isFavourite").GetBoolean().Should().BeFalse();
                item.GetProperty("lastOpenedAt").ValueKind.Should().Be(JsonValueKind.Null);
            }
            finally
            {
                await owner.Client.DeleteAsync("/user");
                await other.Client.DeleteAsync("/user");
            }
        }

        [Fact]
        public async Task RenamingOrMoving_KeepsFavouriteAndLastOpened()
        {
            var (client, _, _) = await NewUserAsync();
            try
            {
                var id = await UploadTextAsync(client, "keep.txt");
                var folderId = await TestAccounts.CreateFolderAsync(client, "Archive");
                await client.PutAsync($"/files/{id}/favourite", null);
                await client.PostAsync($"/files/{id}/opened", null);

                (await client.PatchAsync("/rename", JsonBody(new { id, newName = "kept.txt" }))).StatusCode.Should().Be(HttpStatusCode.OK);
                (await client.PutAsync("/move", JsonBody(new { sourceIds = new[] { id }, destinationId = folderId }))).StatusCode.Should().Be(HttpStatusCode.OK);

                var item = await TestAccounts.GetItemAsync(client, id);
                item.GetProperty("path").GetString().Should().Be("/Archive/kept.txt");
                item.GetProperty("isFavourite").GetBoolean().Should().BeTrue();
                item.GetProperty("lastOpenedAt").ValueKind.Should().Be(JsonValueKind.String);
            }
            finally
            {
                await client.DeleteAsync("/user");
            }
        }

        private static StringContent JsonBody(object value) =>
            new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
    }
}
