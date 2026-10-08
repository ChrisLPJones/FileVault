import { api } from "./api";

// Image types the server makes thumbnails for (see ThumbnailService)
const THUMBNAIL_EXTENSIONS = ["jpg", "jpeg", "png", "gif", "webp", "bmp"];

export const canHaveThumbnail = (file) => {
    if (!file || file.isDirectory || !file._id) return false;
    const dot = file.name.lastIndexOf(".");
    return dot > 0 && THUMBNAIL_EXTENSIONS.includes(file.name.slice(dot + 1).toLowerCase());
};

// A small WebP preview of an image file, as a Blob. Rejects with a 404 if there isn't one.
export const fetchThumbnailBlob = async (fileId) =>
    (await api.get(`/files/${encodeURIComponent(fileId)}/thumbnail`, { responseType: "blob" })).data;
