using Backend.Models;

namespace Backend.Services;

// Deleting an account, wherever it is asked for from (DELETE /user, DELETE /admin/users/{id}):
// the account and its files rows in one guarded transaction (the last administrator is refused),
// then thumbnails, encrypted files, the avatar, unfinished chunked uploads, and finally anything
// the access-token check remembers about the user.
public class AccountDeletionService(
    DatabaseServices db,
    FileServices files,
    AvatarService avatars,
    ChunkedUploadService uploads,
    AccessTokenGate tokenGate)
{
    public async Task<HttpReturnResult> DeleteAsync(string userId)
    {
        var result = await db.DeleteUserAndFilesById(userId, files);
        if (!result.Success)
            return result;

        avatars.DeleteFile(userId);
        uploads.DeleteUserTempFiles(userId);
        tokenGate.Evict(userId);
        return result;
    }
}
