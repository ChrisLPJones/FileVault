using System.Text.Json;

namespace Backend.Models
{
    // DELETE /delete body: { "ids": "one-id" } or { "ids": ["id1", "id2"] }
    public class DeleteRequest
    {
        public JsonElement ids { get; set; }
    }
}
