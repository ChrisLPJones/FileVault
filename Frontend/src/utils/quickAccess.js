// How many recently opened files the folder tree lists
export const RECENT_LIMIT = 10;

// Starred items: folders first, then files, each by name
export const favouriteItems = (files = []) =>
    files
        .filter((file) => file.isFavourite)
        .sort((a, b) => (a.isDirectory === b.isDirectory ? a.name.localeCompare(b.name) : a.isDirectory ? -1 : 1));

// The most recently opened files, newest first
export const recentFiles = (files = [], limit = RECENT_LIMIT) =>
    files
        .filter((file) => !file.isDirectory && file.lastOpenedAt && !Number.isNaN(Date.parse(file.lastOpenedAt)))
        .sort((a, b) => Date.parse(b.lastOpenedAt) - Date.parse(a.lastOpenedAt))
        .slice(0, limit);
