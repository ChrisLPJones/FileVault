import { formatBytes } from "./formatBytes";

// What is left of the storage quota. usage: { used, quota } in bytes (used includes the recycle bin);
// reservedBytes: files already queued or uploading that the server hasn't counted yet.
// Returns null when the quota isn't known (the server still refuses uploads that don't fit).
export const spaceLeft = (usage, reservedBytes = 0) => {
    if (!usage || !(usage.quota > 0)) return null;
    return Math.max(0, usage.quota - (usage.used ?? 0) - reservedBytes);
};

export const notEnoughSpaceMessage = (size, left) =>
    `Not enough space: this file is ${formatBytes(size)} and you have ${formatBytes(left)} left. ` +
    "Files in the recycle bin still count, so empty it to free space.";

// Checks files in order against the space left; each one that fits uses some of it before the next
// is checked, so a later file can be refused because of an earlier one. sizes: bytes per file.
// Returns an error message (or null if it fits) for each file.
export const checkUploadSpace = (sizes, usage, reservedBytes = 0) => {
    let left = spaceLeft(usage, reservedBytes);
    return sizes.map((size) => {
        if (left === null) return null;
        if (size > left) return notEnoughSpaceMessage(size, left);
        left -= size;
        return null;
    });
};

// Whether the queued file at index still fits, for a retry: checked against the space left after
// the other queued files (the file itself holds nothing back while it is cancelled or failed).
// Returns an error message, or null if it fits.
export const retrySpaceError = (queued, index, usage) => {
    const others = queued.map((item, i) => (i === index ? { ...item, cancelled: true } : item));
    return checkUploadSpace([queued[index].file.size], usage, reservedBytes(others))[0];
};

// Bytes the queued files will take that the server hasn't counted yet. Files that failed, were
// removed or cancelled, or are already in the usage figure (counted) hold nothing back.
export const reservedBytes = (queued) =>
    queued.reduce(
        (sum, { file, error, removed, counted, cancelled }) =>
            error || removed || counted || cancelled ? sum : sum + file.size,
        0
    );
