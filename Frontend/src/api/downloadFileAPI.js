import { api } from "./api";

// Fetch one stored file as a Blob (used for downloads and previews)
export const fetchFileBlob = async (fileId) => {
    const response = await api.get(`/download/${encodeURIComponent(fileId)}`, {
        responseType: "blob",
    });
    return response.data;
};

const saveBlob = (blob, fileName) => {
    const downloadUrl = URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = downloadUrl;
    link.download = fileName;
    document.body.appendChild(link);
    link.click();
    link.remove();
    URL.revokeObjectURL(downloadUrl);
};

// One file downloads as-is; several items or any folder download as a single zip
export const downloadFile = async (files) => {
    if (files.length === 0) return;

    if (files.length === 1 && !files[0].isDirectory) {
        saveBlob(await fetchFileBlob(files[0]._id), files[0].name);
        return;
    }

    const response = await api.post(
        "/download/zip",
        { ids: files.map((file) => file._id) },
        { responseType: "blob" }
    );
    const zipName =
        files.length === 1 ? `${files[0].name}.zip` : "FileVault.zip";
    saveBlob(response.data, zipName);
};
