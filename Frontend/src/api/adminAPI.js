import { api } from "./api";

// { isAdmin } for the logged-in user
export const getAdminStatusAPI = async () => (await api.get("/admin/me")).data;

// [{ id, firstName, lastName, email, createdAt, lastLogin, bytesUsed, fileCount, quota, quotaOverride, isAdmin }]
export const getAdminUsersAPI = async () => (await api.get("/admin/users")).data;

// { userCount, adminCount, fileCount, folderCount, totalStoredBytes, defaultQuotaBytes,
//   storageBytesOnDisk, diskTotalBytes, diskFreeBytes }
export const getAdminStatsAPI = async () => (await api.get("/admin/stats")).data;

// quotaBytes: a number of bytes, or null for the server's default
export const setUserQuotaAPI = (userId, quotaBytes) =>
    api.patch(`/admin/users/${encodeURIComponent(userId)}/quota`, { quotaBytes });
