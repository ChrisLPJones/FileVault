import { api } from "./api";

const itemUrl = (fileId) => `/files/${encodeURIComponent(fileId)}`;

// Star or unstar a file or folder
export const setFavouriteAPI = (fileId, favourite) =>
    favourite ? api.put(`${itemUrl(fileId)}/favourite`) : api.delete(`${itemUrl(fileId)}/favourite`);

// Record that a file was previewed or downloaded (for the Recent list)
export const markOpenedAPI = (fileId) => api.post(`${itemUrl(fileId)}/opened`);
