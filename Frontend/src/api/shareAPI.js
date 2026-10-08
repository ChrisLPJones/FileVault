import { api, API_BASE_URL } from "./api";

// Share links. The token is only returned when a link is created, so the full link
// can't be shown again later (only a hash of it is stored).

// The address to give out for a link: the share page on this site
export const shareUrl = (token) => `${window.location.origin}/s/${token}`;

// { id, token, path, itemId, name, isDirectory, createdAt, expiresAt, hasPassword }
export const createShareAPI = async (itemId, { expiresAt = null, password = "" } = {}) =>
    (await api.post("/shares", { itemId, expiresAt, password: password || null })).data;

// [{ id, itemId, name, isDirectory, createdAt, expiresAt, hasPassword, downloadCount }]
export const getSharesAPI = async () => (await api.get("/shares")).data;

export const revokeShareAPI = async (id) => api.delete(`/shares/${encodeURIComponent(id)}`);

// Public endpoints: no access token, and a 401 here means a wrong link password, not a logged-out user
const publicRequest = { skipAuth: true, withCredentials: false };

// { passwordRequired, name?, size?, isDirectory?, expiresAt?, files? }
export const getPublicShareAPI = async (token) =>
    (await api.get(`/s/${encodeURIComponent(token)}`, publicRequest)).data;

// Same details for a password-protected link
export const unlockShareAPI = async (token, password) =>
    (await api.post(`/s/${encodeURIComponent(token)}`, { password }, publicRequest)).data;

// Where the share page posts its download form (a form post lets the browser stream the file to disk)
export const shareDownloadUrl = (token) => `${API_BASE_URL}/s/${encodeURIComponent(token)}/download`;
