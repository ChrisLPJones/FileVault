import { api } from "./api";

// { isAdmin } for the logged-in user
export const getAdminStatusAPI = async () => (await api.get("/admin/me")).data;

// [{ id, firstName, lastName, email, createdAt, lastLogin, bytesUsed, fileCount, quota, quotaOverride,
//    isAdmin, isPermanent, avatarUpdatedAt, suspendedAt, lastActiveAt, removalDueAt (hosted mode only) }]
export const getAdminUsersAPI = async () => (await api.get("/admin/users")).data;

// { userCount, adminCount, fileCount, folderCount, totalStoredBytes, defaultQuotaBytes,
//   storageBytesOnDisk, diskTotalBytes, diskFreeBytes, currentUserId, suspendedCount, mode: "self-hosted" | "hosted" }
export const getAdminStatsAPI = async () => (await api.get("/admin/stats")).data;

// quotaBytes: a number of bytes, or null for the server's default
export const setUserQuotaAPI = (userId, quotaBytes) =>
    api.patch(`/admin/users/${encodeURIComponent(userId)}/quota`, { quotaBytes });

// Grant or remove administrator rights. Removing them from the last administrator is a 409.
export const setUserAdminAPI = (userId, isAdmin) =>
    api.put(`/admin/users/${encodeURIComponent(userId)}/admin`, { isAdmin });

// Create an account that works straight away (its email counts as confirmed).
// 400 for an invalid name, email or password, 409 if the email is taken.
export const createUserAPI = async ({ firstName, lastName, email, password, isAdmin = false, isPermanent = false }) =>
    (await api.post("/admin/users", { firstName, lastName, email, password, isAdmin, isPermanent })).data;

// Delete another user's account and all their files. 409 for the last administrator.
export const deleteUserAPI = (userId) => api.delete(`/admin/users/${encodeURIComponent(userId)}`);

// Set a user's password; they are signed out everywhere straight away
export const setUserPasswordAPI = (userId, password) =>
    api.put(`/admin/users/${encodeURIComponent(userId)}/password`, { password });

// Mark an account permanent (skipped by inactive-account removal) or not
export const setUserPermanentAPI = (userId, isPermanent) =>
    api.put(`/admin/users/${encodeURIComponent(userId)}/permanent`, { isPermanent });

// Suspend an account (it can't sign in; its files are kept) or lift the suspension. 409 for the
// last administrator; 400 for your own account.
export const setUserSuspendedAPI = (userId, suspended) =>
    api.put(`/admin/users/${encodeURIComponent(userId)}/suspended`, { suspended });

// A user's profile picture as a Blob (404 if they have none). Fetched through the API client so the
// access token travels in the Authorization header, never in an image URL.
export const getUserAvatarBlobAPI = async (userId) =>
    (await api.get(`/admin/users/${encodeURIComponent(userId)}/avatar`, { responseType: "blob" })).data;
