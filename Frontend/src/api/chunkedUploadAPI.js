import { api } from "./api";

// Files above this size are sent in chunks (POST /uploads ...); smaller ones use POST /upload
export const CHUNKED_UPLOAD_THRESHOLD = 8 * 1024 * 1024;

const MAX_ATTEMPTS = 4; // per chunk
const retryDelay = (attempt) => 1000 * 2 ** (attempt - 1); // 1 s, 2 s, 4 s

const wait = (ms, signal) =>
    new Promise((resolve, reject) => {
        const timer = setTimeout(resolve, ms);
        signal?.addEventListener("abort", () => {
            clearTimeout(timer);
            reject(new DOMException("Aborted", "AbortError"));
        });
    });

// Network errors, timeouts, rate limits and server errors are worth another try; other errors aren't
const isRetryable = (error) =>
    !error.response || error.response.status === 429 || error.response.status >= 500;

// Upload a file in chunks, resuming `uploadId` if given (only the chunks the server is missing
// are sent). Each chunk is retried a few times before giving up. If it fails, the error has the
// uploadId so the caller can resume later. Returns the server's { success, id, name }.
export const uploadInChunks = async (file, { parentId, uploadId, onProgress, onStarted, signal } = {}) => {
    let chunkSize;
    let chunkCount;
    let received = new Set();

    if (uploadId) {
        const status = (await api.get(`/uploads/${uploadId}`, { signal })).data;
        ({ chunkSize, chunkCount } = status);
        received = new Set(status.receivedChunks);
    } else {
        const started = (
            await api.post(
                "/uploads",
                { name: file.name, size: file.size, parentId: parentId || null, mimeType: file.type || null },
                { signal }
            )
        ).data;
        ({ uploadId, chunkSize, chunkCount } = started);
        onStarted?.(uploadId);
    }

    const chunkBytes = (index) => Math.min(chunkSize, file.size - index * chunkSize);
    let done = [...received].reduce((sum, index) => sum + chunkBytes(index), 0);
    onProgress?.(done / file.size);

    try {
        for (let index = 0; index < chunkCount; index++) {
            if (received.has(index)) continue;
            const blob = file.slice(index * chunkSize, index * chunkSize + chunkBytes(index));

            for (let attempt = 1; ; attempt++) {
                try {
                    await api.put(`/uploads/${uploadId}/chunks/${index}`, blob, {
                        headers: { "Content-Type": "application/octet-stream" },
                        signal,
                        onUploadProgress: (event) => onProgress?.((done + (event.loaded ?? 0)) / file.size),
                    });
                    break;
                } catch (error) {
                    if (signal?.aborted || attempt >= MAX_ATTEMPTS || !isRetryable(error)) throw error;
                    await wait(retryDelay(attempt), signal);
                }
            }

            done += chunkBytes(index);
            onProgress?.(done / file.size);
        }

        return (await api.post(`/uploads/${uploadId}/complete`, null, { signal })).data;
    } catch (error) {
        error.uploadId = uploadId;
        throw error;
    }
};

// Cancel an upload and delete its chunks on the server (best effort)
export const cancelChunkedUpload = (uploadId) => api.delete(`/uploads/${uploadId}`).catch(() => {});
