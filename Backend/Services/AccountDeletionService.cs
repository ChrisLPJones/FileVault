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
    // onlyIfInactive: only delete the account if it is still due for removal for inactivity
    // (hosted mode), checked inside the delete transaction
    public async Task<HttpReturnResult> DeleteAsync(string userId, HostedOptions? onlyIfInactive = null)
    {
        var result = await db.DeleteUserAndFilesById(userId, files, onlyIfInactive);
        if (!result.Success)
            return result;

        avatars.DeleteFile(userId);
        uploads.DeleteUserTempFiles(userId);
        tokenGate.Evict(userId);
        return result;
    }
}
