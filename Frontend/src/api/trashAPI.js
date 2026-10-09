import { api } from "./api";

// The recycle bin. Deleting a file or folder (deleteAPI) moves it here.

// [{ _id, name, isDirectory, originalFolder, size, deletedAt, purgeAt }]
export const getTrashAPI = async () => (await api.get("/trash")).data;

export const restoreTrashAPI = async (ids) => api.post("/trash/restore", { ids });

export const deleteFromTrashAPI = async (id) => api.delete(`/trash/${encodeURIComponent(id)}`);

export const emptyTrashAPI = async () => api.delete("/trash");
